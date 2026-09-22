using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Data;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Tests.Integration;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using Empty = EventStore.Client.Empty;
using GrpcExceptions = EventStore.Core.Services.Transport.Grpc.Constants.Exceptions;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;

namespace EventStore.Core.Tests.Replication.ReadOnlyReplica;

[Category("LongRunning")]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class connecting_to_read_only_replica<TLogFormat, TStreamId> : specification_with_cluster<TLogFormat, TStreamId>
{
	protected override async Task Given()
	{
		await _nodes[2].AdminUserCreated.WithTimeout(TimeSpan.FromSeconds(30));
		AssertEx.IsOrBecomesTrue(() => _nodes[2].NodeState == VNodeState.ReadOnlyReplica,
			timeout: TimeSpan.FromSeconds(30),
			onFail: MiniNodeLogging.WriteLogs);
	}

	protected override MiniClusterNode<TLogFormat, TStreamId> CreateNode(int index, Endpoints endpoints, EndPoint[] gossipSeeds,
		bool wait = true)
	{
		var isReadOnly = index == 2;
		var node = new MiniClusterNode<TLogFormat, TStreamId>(
			PathName, index, endpoints.NodeEndPoint, endpoints.ReplicationEndPoint, gossipSeeds,
			readOnlyReplica: isReadOnly);
		if (wait && !isReadOnly)
		{
			WaitIdle();
		}

		return node;
	}

	private static CallOptions GetCallOptions()
	{
		var credentials = CallCredentials.FromInterceptor((_, metadata) =>
		{
			metadata.Add("authorization",
				$"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:changeit"))}");
			return Task.CompletedTask;
		});
		return new CallOptions(credentials: credentials, deadline: DateTime.UtcNow.AddSeconds(30));
	}

	private static Streams.StreamsClient CreateClient(
		MiniClusterNode<TLogFormat, TStreamId> node,
		out GrpcChannel channel,
		out HttpClient httpClient)
	{
		httpClient = new HttpClient(new SocketsHttpHandler
		{
			SslOptions = { RemoteCertificateValidationCallback = delegate { return true; } }
		});
		channel = GrpcChannel.ForAddress(new Uri($"https://{node.HttpEndPoint}"),
			new GrpcChannelOptions { HttpClient = httpClient });
		return new Streams.StreamsClient(channel);
	}

	[Test]
	public async Task append_to_stream_is_rejected()
	{
		var client = CreateClient(_nodes[2], out var channel, out var httpClient);
		using (channel)
		using (httpClient)
		using (var call = client.Append(GetCallOptions()))
		{
			await call.RequestStream.WriteAsync(new AppendReq
			{
				Options = new()
				{
					Any = new Empty(),
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(nameof(append_to_stream_is_rejected)) }
				}
			});
			await call.RequestStream.WriteAsync(new AppendReq
			{
				ProposedMessage = new()
				{
					Id = Uuid.NewUuid().ToDto(),
					Data = ByteString.Empty,
					CustomMetadata = ByteString.Empty,
					Metadata =
					{
						[GrpcMetadata.Type] = "test",
						[GrpcMetadata.ContentType] = GrpcMetadata.ContentTypes.ApplicationJson
					}
				}
			});
			await call.RequestStream.CompleteAsync();

			var exception = Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
			Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.NotFound));
			Assert.That(exception.Trailers.Select(x => (x.Key, x.Value)),
				Does.Contain((GrpcExceptions.ExceptionKey, GrpcExceptions.NotLeader)));
		}
	}

	[Test]
	public async Task batch_append_is_rejected()
	{
		var client = CreateClient(_nodes[2], out var channel, out var httpClient);
		using (channel)
		using (httpClient)
		using (var call = client.BatchAppend(GetCallOptions()))
		{
			await call.RequestStream.WriteAsync(new BatchAppendReq
			{
				CorrelationId = Uuid.NewUuid().ToDto(),
				Options = new()
				{
					Any = new Google.Protobuf.WellKnownTypes.Empty(),
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(nameof(batch_append_is_rejected)) }
				},
				IsFinal = true,
				ProposedMessages =
				{
					new BatchAppendReq.Types.ProposedMessage
					{
						Id = Uuid.NewUuid().ToDto(),
						Metadata =
						{
							[GrpcMetadata.Type] = "test",
							[GrpcMetadata.ContentType] = GrpcMetadata.ContentTypes.ApplicationJson
						}
					},
					new BatchAppendReq.Types.ProposedMessage
					{
						Id = Uuid.NewUuid().ToDto(),
						Metadata =
						{
							[GrpcMetadata.Type] = "test",
							[GrpcMetadata.ContentType] = GrpcMetadata.ContentTypes.ApplicationJson
						}
					}
				}
			});
			await call.RequestStream.CompleteAsync();

			var exception = Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext());
			Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.NotFound));
			Assert.That(exception.Trailers.Select(x => (x.Key, x.Value)),
				Does.Contain((GrpcExceptions.ExceptionKey, GrpcExceptions.NotLeader)));
		}
	}

	[Test]
	public async Task delete_stream_is_rejected()
	{
		const string stream = nameof(delete_stream_is_rejected);
		var leader = GetLeader();
		await AppendToStream(leader, stream);
		var leaderWriterPosition = leader.Db.Config.WriterCheckpoint.Read();
		AssertEx.IsOrBecomesTrue(
			() => _nodes[2].Db.Config.WriterCheckpoint.Read() >= leaderWriterPosition,
			timeout: TimeSpan.FromSeconds(30),
			onFail: MiniNodeLogging.WriteLogs,
			msg: "The stream was not replicated to the read-only replica.");

		var client = CreateClient(_nodes[2], out var channel, out var httpClient);
		using (channel)
		using (httpClient)
		using (var call = client.DeleteAsync(new DeleteReq
		{
			Options = new()
			{
				Any = new Empty(),
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(stream) }
			}
		}, GetCallOptions()))
		{
			var exception = Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
			Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.NotFound));
			Assert.That(exception.Trailers.Select(x => (x.Key, x.Value)),
				Does.Contain((GrpcExceptions.ExceptionKey, GrpcExceptions.NotLeader)));
		}
	}

	private static async Task AppendToStream(
		MiniClusterNode<TLogFormat, TStreamId> node,
		string stream)
	{
		var client = CreateClient(node, out var channel, out var httpClient);
		using (channel)
		using (httpClient)
		using (var call = client.Append(GetCallOptions()))
		{
			await call.RequestStream.WriteAsync(new AppendReq
			{
				Options = new()
				{
					NoStream = new Empty(),
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(stream) }
				}
			});
			await call.RequestStream.WriteAsync(new AppendReq
			{
				ProposedMessage = new()
				{
					Id = Uuid.NewUuid().ToDto(),
					Data = ByteString.Empty,
					CustomMetadata = ByteString.Empty,
					Metadata =
					{
						[GrpcMetadata.Type] = "test",
						[GrpcMetadata.ContentType] = GrpcMetadata.ContentTypes.ApplicationJson
					}
				}
			});
			await call.RequestStream.CompleteAsync();

			var response = await call.ResponseAsync;
			Assert.That(response.ResultCase, Is.EqualTo(AppendResp.ResultOneofCase.Success));
		}
	}
}
