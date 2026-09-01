using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Streams;
using EventStore.Core.Data;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Tests.Helpers;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;

namespace EventStore.Core.Tests.Integration;

[Category("LongRunning")]
[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class grpc_request_forwarding_survives_leader_change<TLogFormat, TStreamId>
	: specification_with_cluster<TLogFormat, TStreamId>
{
	private const string Stream = "$grpc-forwarding-failover";
	private const string AuthorizationHeaderValue = "Basic YWRtaW46Y2hhbmdlaXQ=";
	private static readonly TimeSpan ClusterTransitionTimeout = TimeSpan.FromMinutes(2);

	[Test]
	public async Task completes_writes_through_a_surviving_follower_after_a_new_leader_is_elected()
	{
		AssertEx.IsOrBecomesTrue(
			() =>
				_nodes.Count(node => node.NodeState == VNodeState.Leader) == 1 &&
				_nodes.Count(node => node.NodeState == VNodeState.Follower) == 2,
			ClusterTransitionTimeout,
			"The initial cluster topology did not stabilize",
			MiniNodeLogging.WriteLogs);

		var initialLeader = _nodes.Single(node => node.NodeState == VNodeState.Leader);
		var initialFollowers = _nodes.Where(node => node.NodeState == VNodeState.Follower).ToArray();
		Assert.That(await Append(initialFollowers[0].HttpEndPoint, ExpectedStreamRevision.NoStream), Is.EqualTo(0));
		Assert.That(await Append(initialFollowers[1].HttpEndPoint, ExpectedStreamRevision.Exact(0)), Is.EqualTo(1));

		await initialLeader.Shutdown(keepDb: true);
		_nodes[initialLeader.DebugIndex] = null;

		AssertEx.IsOrBecomesTrue(
			() =>
				_nodes.Count(node => node is not null && node.NodeState == VNodeState.Leader) == 1 &&
				_nodes.Count(node => node is not null && node.NodeState == VNodeState.Follower) == 1,
			ClusterTransitionTimeout,
			"The surviving nodes did not elect a leader",
			MiniNodeLogging.WriteLogs);

		var forwardingFollower = _nodes.Single(node => node is not null && node.NodeState == VNodeState.Follower);
		Assert.That(initialFollowers, Does.Contain(forwardingFollower));
		Assert.That(await Append(forwardingFollower.HttpEndPoint, ExpectedStreamRevision.Exact(1)), Is.EqualTo(2));
	}

	private static async Task<ulong> Append(IPEndPoint endpoint, ExpectedStreamRevision expectedRevision)
	{
		using var handler = new SocketsHttpHandler
		{
			SslOptions =
			{
				RemoteCertificateValidationCallback = delegate { return true; }
			}
		};
		using var httpClient = new HttpClient(handler);
		using var channel = GrpcChannel.ForAddress(
			new Uri($"https://{endpoint}"),
			new GrpcChannelOptions { HttpClient = httpClient });
		var client = new Streams.StreamsClient(channel);
		using var call = client.Append(new CallOptions(
			credentials: CallCredentials.FromInterceptor((_, metadata) =>
			{
				metadata.Add("authorization", AuthorizationHeaderValue);
				return Task.CompletedTask;
			}),
			deadline: DateTime.UtcNow.AddSeconds(30)));

		var options = new AppendReq.Types.Options
		{
			StreamIdentifier = new StreamIdentifier
			{
				StreamName = ByteString.CopyFromUtf8(Stream)
			}
		};
		switch (expectedRevision.Kind)
		{
			case ExpectedStreamRevisionKind.NoStream:
				options.NoStream = new Empty();
				break;
			case ExpectedStreamRevisionKind.Exact:
				options.Revision = expectedRevision.Value;
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(expectedRevision));
		}

		await call.RequestStream.WriteAsync(new AppendReq { Options = options });
		await call.RequestStream.WriteAsync(new AppendReq
		{
			ProposedMessage = new AppendReq.Types.ProposedMessage
			{
				Id = Uuid.NewUuid().ToDto(),
				CustomMetadata = ByteString.Empty,
				Data = ByteString.Empty,
				Metadata =
				{
					[GrpcMetadata.Type] = "failover-test",
					[GrpcMetadata.ContentType] = GrpcMetadata.ContentTypes.ApplicationOctetStream
				}
			}
		});
		await call.RequestStream.CompleteAsync();

		var response = await call.ResponseAsync;
		Assert.That(response.ResultCase, Is.EqualTo(AppendResp.ResultOneofCase.Success));
		return response.Success.CurrentRevision;
	}

	private enum ExpectedStreamRevisionKind
	{
		NoStream,
		Exact
	}

	private readonly record struct ExpectedStreamRevision(ExpectedStreamRevisionKind Kind, ulong Value)
	{
		public static ExpectedStreamRevision NoStream { get; } = new(ExpectedStreamRevisionKind.NoStream, 0);

		public static ExpectedStreamRevision Exact(ulong value) => new(ExpectedStreamRevisionKind.Exact, value);
	}
}
