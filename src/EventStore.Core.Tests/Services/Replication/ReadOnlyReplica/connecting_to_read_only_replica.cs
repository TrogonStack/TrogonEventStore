using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Tests.Integration;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using Empty = EventStore.Client.Empty;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;

namespace EventStore.Core.Tests.Replication.ReadOnlyReplica;

[Category("LongRunning")]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class connecting_to_read_only_replica<TLogFormat, TStreamId> : specification_with_cluster<TLogFormat, TStreamId>
{
	protected override async Task Given() =>
		await _nodes[2].AdminUserCreated.WithTimeout(TimeSpan.FromSeconds(30));

	protected override MiniClusterNode<TLogFormat, TStreamId> CreateNode(int index, Endpoints endpoints, EndPoint[] gossipSeeds,
		bool wait = true)
	{
		var isReadOnly = index == 2;
		var node = new MiniClusterNode<TLogFormat, TStreamId>(
			PathName, index, endpoints.NodeEndPoint, gossipSeeds,
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

	private Streams.StreamsClient CreateClient(out GrpcChannel channel, out HttpClient httpClient)
	{
		httpClient = new HttpClient(new SocketsHttpHandler
		{
			SslOptions = { RemoteCertificateValidationCallback = delegate { return true; } }
		});
		channel = GrpcChannel.ForAddress(new Uri($"https://{_nodes[2].HttpEndPoint}"),
			new GrpcChannelOptions { HttpClient = httpClient });
		return new Streams.StreamsClient(channel);
	}

	[Test]
	public async Task append_to_stream_is_rejected()
	{
		var client = CreateClient(out var channel, out var httpClient);
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
		}
	}

	[Test]
	public async Task delete_stream_is_rejected()
	{
		var client = CreateClient(out var channel, out var httpClient);
		using (channel)
		using (httpClient)
		using (var call = client.DeleteAsync(new DeleteReq
		{
			Options = new()
			{
				Any = new Empty(),
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(nameof(delete_stream_is_rejected)) }
			}
		}, GetCallOptions()))
		{
			var exception = Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
			Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.NotFound));
		}
	}
}
