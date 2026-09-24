using System;
using System.Collections.Generic;
using System.Diagnostics;
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
	private const int TestTimeoutMilliseconds = 8 * 60 * 1000;
	private static readonly TimeSpan AuthenticationRetryDelay = TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromMinutes(7);

	[Test]
	[Timeout(TestTimeoutMilliseconds)]
	public async Task replicates_writes_across_leader_loss_and_reconnection()
	{
		var scenario = Stopwatch.StartNew();
		AssertEx.IsOrBecomesTrue(
			() =>
				_nodes.Count(node => node.NodeState == VNodeState.Leader) == 1 &&
				_nodes.Count(node => node.NodeState == VNodeState.Follower) == 2,
			RemainingScenarioTime(scenario),
			"The initial cluster topology did not stabilize",
			MiniNodeLogging.WriteLogs);

		var initialLeader = _nodes.Single(node => node.NodeState == VNodeState.Leader);
		var initialFollowers = _nodes.Where(node => node.NodeState == VNodeState.Follower).ToArray();
		Assert.That(await Append(initialFollowers[0].HttpEndPoint, ExpectedStreamRevision.NoStream, scenario),
			Is.EqualTo(0));
		Assert.That(await Append(initialFollowers[1].HttpEndPoint, ExpectedStreamRevision.Exact(0), scenario),
			Is.EqualTo(1));
		foreach (var node in _nodes)
		{
			await AssertRevisions(node.HttpEndPoint, [0, 1], scenario);
		}

		await initialLeader.Shutdown(keepDb: true);
		_nodes[initialLeader.DebugIndex] = null;

		AssertEx.IsOrBecomesTrue(
			() =>
				_nodes.Count(node => node is not null && node.NodeState == VNodeState.Leader) == 1 &&
				_nodes.Count(node => node is not null && node.NodeState == VNodeState.Follower) == 1,
			RemainingScenarioTime(scenario),
			"The surviving nodes did not elect a leader",
			MiniNodeLogging.WriteLogs);

		var forwardingFollower = _nodes.Single(node => node is not null && node.NodeState == VNodeState.Follower);
		Assert.That(initialFollowers, Does.Contain(forwardingFollower));
		Assert.That(await Append(forwardingFollower.HttpEndPoint, ExpectedStreamRevision.Exact(1), scenario),
			Is.EqualTo(2));
		foreach (var node in _nodes.Where(node => node is not null))
		{
			await AssertRevisions(node.HttpEndPoint, [0, 1, 2], scenario);
		}

		var restartedLeaderIndex = initialLeader.DebugIndex;
		var restartedLeader = CreateNode(
			restartedLeaderIndex,
			_nodeEndpoints[restartedLeaderIndex],
			_nodeEndpoints.Where((_, index) => index != restartedLeaderIndex)
				.Select(endpoints => (EndPoint)endpoints.ClusterEndPoint)
				.ToArray());
		_nodes[restartedLeaderIndex] = restartedLeader;
		restartedLeader.Start();

		AssertEx.IsOrBecomesTrue(
			() =>
				_nodes.Count(node => node.NodeState == VNodeState.Leader) == 1 &&
				_nodes.Count(node => node.NodeState == VNodeState.Follower) == 2,
			RemainingScenarioTime(scenario),
			"The reconnected node did not rejoin the cluster",
			MiniNodeLogging.WriteLogs);

		foreach (var node in _nodes)
		{
			await AssertRevisions(node.HttpEndPoint, [0, 1, 2], scenario);
		}
	}

	private static async Task AssertRevisions(IPEndPoint endpoint, ulong[] expected, Stopwatch scenario)
	{
		while (true)
		{
			try
			{
				var actual = await ReadRevisions(endpoint, RemainingScenarioTime(scenario));
				if (actual.Count >= expected.Length)
				{
					Assert.That(actual, Is.EqualTo(expected), $"Replication differed at {endpoint}");
					return;
				}
			}
			catch (RpcException ex) when (
				ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded &&
				scenario.Elapsed < ScenarioTimeout)
			{
			}

			await Task.Delay(AuthenticationRetryDelay);
		}
	}

	private static async Task<List<ulong>> ReadRevisions(IPEndPoint endpoint, TimeSpan remainingScenarioTime)
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
		using var call = client.Read(new ReadReq
		{
			Options = new ReadReq.Types.Options
			{
				Stream = new ReadReq.Types.Options.Types.StreamOptions
				{
					StreamIdentifier = new StreamIdentifier
					{
						StreamName = ByteString.CopyFromUtf8(Stream)
					},
					Start = new Empty()
				},
				ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Forwards,
				Count = 3,
				NoFilter = new Empty(),
				UuidOption = new ReadReq.Types.Options.Types.UUIDOption { Structured = new Empty() }
			}
		}, new CallOptions(
			credentials: CallCredentials.FromInterceptor((_, metadata) =>
			{
				metadata.Add("authorization", AuthorizationHeaderValue);
				return Task.CompletedTask;
			}),
			deadline: DateTime.UtcNow.Add(remainingScenarioTime < RequestTimeout
				? remainingScenarioTime
				: RequestTimeout)));

		var revisions = new List<ulong>();
		await foreach (var response in call.ResponseStream.ReadAllAsync())
		{
			if (response.Event is { } readEvent)
			{
				revisions.Add(readEvent.Event.StreamRevision);
			}
		}
		return revisions;
	}

	private static async Task<ulong> Append(
		IPEndPoint endpoint,
		ExpectedStreamRevision expectedRevision,
		Stopwatch scenario)
	{
		while (true)
		{
			try
			{
				return await AppendOnce(endpoint, expectedRevision, RemainingScenarioTime(scenario));
			}
			catch (RpcException ex) when (
				ex.StatusCode is StatusCode.Unauthenticated or StatusCode.Unavailable &&
				scenario.Elapsed < ScenarioTimeout)
			{
				await Task.Delay(AuthenticationRetryDelay);
			}
		}
	}

	private static async Task<ulong> AppendOnce(
		IPEndPoint endpoint,
		ExpectedStreamRevision expectedRevision,
		TimeSpan remainingScenarioTime)
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
			deadline: DateTime.UtcNow.Add(remainingScenarioTime < RequestTimeout
				? remainingScenarioTime
				: RequestTimeout)));

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

	private static TimeSpan RemainingScenarioTime(Stopwatch scenario)
	{
		var remaining = ScenarioTimeout - scenario.Elapsed;
		return remaining > TimeSpan.Zero
			? remaining
			: throw new TimeoutException("The forwarding failover scenario exceeded its time budget");
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
