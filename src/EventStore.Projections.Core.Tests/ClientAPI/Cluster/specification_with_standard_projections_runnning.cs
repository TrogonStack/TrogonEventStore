using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Streams;
using EventStore.Common.Options;
using EventStore.Core.Data;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Tests;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Util;
using EventStore.Projections.Core.Services.Processing;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;
using StatisticsReq = EventStore.Client.Projections.StatisticsReq;
using StreamsClient = EventStore.Client.Streams.Streams.StreamsClient;

namespace EventStore.Projections.Core.Tests.ClientAPI.Cluster;

[Category("Grpc")]
public abstract class specification_with_standard_projections_runnning<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
	private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(2);
	private static readonly int PollAttemptCount = (int)(PollTimeout / PollInterval);
	protected MiniClusterNode<TLogFormat, TStreamId>[] _nodes = new MiniClusterNode<TLogFormat, TStreamId>[3];
	protected Endpoints[] _nodeEndpoints = new Endpoints[3];
	private readonly ProjectionsSubsystem[] _projections = new ProjectionsSubsystem[3];
	private HttpClient _streamHttpClient;
	private GrpcChannel _streamChannel;
	private StreamsClient _streams;
	private CancellationToken _operationCancellationToken;
	private protected ProjectionManagementTestClient ProjectionClient;

	protected class Endpoints
	{
		public readonly IPEndPoint InternalTcp;
		public readonly IPEndPoint ExternalTcp;
		public readonly IPEndPoint HttpEndPoint;
		private readonly int[] _ports;

		public Endpoints(int internalTcp, int externalTcp, int httpPort)
		{
			var testIp = Environment.GetEnvironmentVariable("ES-TESTIP");
			var address = string.IsNullOrEmpty(testIp) ? IPAddress.Loopback : IPAddress.Parse(testIp);
			InternalTcp = new IPEndPoint(address, internalTcp);
			ExternalTcp = new IPEndPoint(address, externalTcp);
			HttpEndPoint = new IPEndPoint(address, httpPort);
			_ports = [internalTcp, httpPort, externalTcp];
		}

		public IEnumerable<int> Ports => _ports;
	}

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();

		for (var index = 0; index < _nodeEndpoints.Length; index++)
		{
			_nodeEndpoints[index] = new Endpoints(
				PortsHelper.GetAvailablePort(IPAddress.Loopback),
				PortsHelper.GetAvailablePort(IPAddress.Loopback),
				PortsHelper.GetAvailablePort(IPAddress.Loopback));
		}

		for (var index = 0; index < _nodes.Length; index++)
		{
			var gossipSeeds = _nodeEndpoints
				.Where((_, otherIndex) => otherIndex != index)
				.Select(x => (EndPoint)x.HttpEndPoint)
				.ToArray();
			_nodes[index] = CreateNode(index, _nodeEndpoints[index], gossipSeeds);
		}

		var projectionsStarted = _projections.Select(p => SystemProjections.Created(p.LeaderInputBus)).ToArray();

		foreach (var node in _nodes)
		{
			node.Start();
			node.WaitIdle();
		}

		await Task.WhenAll(_nodes.Select(x => x.Started)).WithTimeout(TimeSpan.FromMinutes(5));
		await Task.WhenAll(_nodes.Select(x => x.AdminUserCreated)).WithTimeout(TimeSpan.FromMinutes(5));

		var leader = _nodes.Single(x => x.NodeState == VNodeState.Leader);
		_streamHttpClient = leader.CreateHttpClient();
		_streamChannel = GrpcChannel.ForAddress(
			_streamHttpClient.BaseAddress,
			new GrpcChannelOptions
			{
				HttpClient = _streamHttpClient,
				DisposeHttpClient = false
			});
		_streams = new StreamsClient(_streamChannel);
		ProjectionClient = new ProjectionManagementTestClient(leader.HttpEndPoint, leader.CreateHttpClient());

		if (GivenStandardProjectionsRunning())
		{
			await Task.WhenAny(projectionsStarted).WithTimeout(OperationTimeout);
			await RunBoundedOperation(EnableStandardProjections);
		}

		await RunBoundedOperation(Given);
		await RunBoundedOperation(When);
	}

	private MiniClusterNode<TLogFormat, TStreamId> CreateNode(int index, Endpoints endpoints, EndPoint[] gossipSeeds)
	{
		_projections[index] = new ProjectionsSubsystem(new ProjectionSubsystemOptions(
			1,
			ProjectionType.All,
			false,
			TimeSpan.FromMinutes(Opts.ProjectionsQueryExpiryDefault),
			Opts.FaultOutOfOrderProjectionsDefault,
			500,
			250));
		return new MiniClusterNode<TLogFormat, TStreamId>(
			PathName,
			index,
			endpoints.InternalTcp,
			endpoints.ExternalTcp,
			endpoints.HttpEndPoint,
			subsystems: [_projections[index]],
			gossipSeeds: gossipSeeds);
	}

	[TearDown]
	public async Task PostTestAsserts()
	{
		var all = await ProjectionClient.StatisticsAll();
		if (all.Any(p => p.Status == "Faulted"))
		{
			Assert.Fail("Projections faulted while running the test" + "\r\n" + string.Join("\r\n", all));
		}
	}

	protected async Task EnableStandardProjections()
	{
		await EnableProjection(ProjectionNamesBuilder.StandardProjections.EventByCategoryStandardProjection);
		await EnableProjection(ProjectionNamesBuilder.StandardProjections.EventByTypeStandardProjection);
		await EnableProjection(ProjectionNamesBuilder.StandardProjections.StreamByCategoryStandardProjection);
		await EnableProjection(ProjectionNamesBuilder.StandardProjections.StreamsStandardProjection);
	}

	protected async Task DisableStandardProjections()
	{
		await DisableProjection(ProjectionNamesBuilder.StandardProjections.EventByCategoryStandardProjection);
		await DisableProjection(ProjectionNamesBuilder.StandardProjections.EventByTypeStandardProjection);
		await DisableProjection(ProjectionNamesBuilder.StandardProjections.StreamByCategoryStandardProjection);
		await DisableProjection(ProjectionNamesBuilder.StandardProjections.StreamsStandardProjection);
	}

	protected virtual bool GivenStandardProjectionsRunning() => true;

	protected async Task EnableProjection(string name)
	{
		for (var attempt = 1; attempt <= 10; attempt++)
		{
			try
			{
				await ProjectionClient.Enable(name, _operationCancellationToken);
				await WaitForProjectionStatus(name, status => status.Contains("Running", StringComparison.OrdinalIgnoreCase));
				return;
			}
			catch when (attempt < 10 && !_operationCancellationToken.IsCancellationRequested)
			{
				await Task.Delay(500, _operationCancellationToken);
			}
		}
	}

	protected async Task DisableProjection(string name)
	{
		await ProjectionClient.Disable(name, cancellationToken: _operationCancellationToken);
		await WaitForProjectionStatus(name, status => status.Contains("Stopped", StringComparison.OrdinalIgnoreCase));
	}

	protected async Task AbortProjection(string name)
	{
		await ProjectionClient.Abort(name, _operationCancellationToken);
		await WaitForProjectionStatus(name, status => status.StartsWith("Aborted", StringComparison.OrdinalIgnoreCase));
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		ProjectionClient?.Dispose();
		_streamChannel?.Dispose();
		_streamHttpClient?.Dispose();

		await Task.WhenAll(_nodes.Where(x => x != null).Select(x => x.Shutdown()));

		await base.TestFixtureTearDown();
	}

	protected virtual Task When() => Task.CompletedTask;

	protected virtual Task Given() => Task.CompletedTask;

	protected async Task PostEvent(string stream, string eventType, string data)
	{
		using var call = _streams.Append(GetCallOptions(_operationCancellationToken));
		await call.RequestStream.WriteAsync(new AppendReq
		{
			Options = new AppendReq.Types.Options
			{
				Any = new Empty(),
				StreamIdentifier = new StreamIdentifier
				{
					StreamName = ByteString.CopyFromUtf8(stream)
				}
			}
		});
		await call.RequestStream.WriteAsync(new AppendReq
		{
			ProposedMessage = new AppendReq.Types.ProposedMessage
			{
				Id = Uuid.NewUuid().ToDto(),
				Data = ByteString.CopyFromUtf8(data),
				CustomMetadata = ByteString.Empty,
				Metadata =
				{
					{ GrpcMetadata.Type, eventType },
					{ GrpcMetadata.ContentType, GrpcMetadata.ContentTypes.ApplicationJson }
				}
			}
		});
		await call.RequestStream.CompleteAsync();
		await call.ResponseAsync;
	}

	protected async Task HardDeleteStream(string stream)
	{
		await _streams.TombstoneAsync(new TombstoneReq
		{
			Options = new TombstoneReq.Types.Options
			{
				Any = new Empty(),
				StreamIdentifier = new StreamIdentifier
				{
					StreamName = ByteString.CopyFromUtf8(stream)
				}
			}
		}, GetCallOptions(_operationCancellationToken));
	}

	protected async Task SoftDeleteStream(string stream)
	{
		await _streams.DeleteAsync(new DeleteReq
		{
			Options = new DeleteReq.Types.Options
			{
				Any = new Empty(),
				StreamIdentifier = new StreamIdentifier
				{
					StreamName = ByteString.CopyFromUtf8(stream)
				}
			}
		}, GetCallOptions(_operationCancellationToken));
	}

	protected void WaitIdle()
	{
		foreach (var node in _nodes)
		{
			node.WaitIdle();
		}

		Thread.Sleep(50);
	}

	protected async Task AssertStreamTailAsync(string streamId, params string[] events)
	{
		string[] actual = [];
		for (var attempt = 0; attempt < PollAttemptCount; attempt++)
		{
			actual = (await ReadStreamBackwards(streamId, (ulong)events.Length))
				.Reverse()
				.Select(x => $"{x.Event.EventType()}:{x.Event.DebugDataView()}")
				.ToArray();
			if (actual.SequenceEqual(events))
			{
				return;
			}

			await Task.Delay(PollInterval);
		}

		Assert.Fail(
			$"Stream '{streamId}' did not reach the expected tail. Expected: [{string.Join(", ", events)}]. Actual: [{string.Join(", ", actual)}].");
	}

	protected async Task DumpStreamAsync(string streamId)
	{
		var events = await ReadStreamBackwards(streamId, 100);
		TestContext.Progress.WriteLine(
			$"Stream '{streamId}': {string.Join(", ", events.Reverse().Select(x => $"{x.Event.EventType()}:{x.Event.DebugDataView()}"))}");
	}

	protected async Task PostProjection(string query)
	{
		await ProjectionClient.CreateContinuous(
			"test-projection",
			query,
			cancellationToken: _operationCancellationToken);
		await WaitForProjectionStatus(
			"test-projection",
			status => status.Contains("Running", StringComparison.OrdinalIgnoreCase));
	}

	private async Task<IReadOnlyList<ReadResp.Types.ReadEvent>> ReadStreamBackwards(string streamId, ulong count)
	{
		using var call = _streams.Read(new ReadReq
		{
			Options = new ReadReq.Types.Options
			{
				Stream = new ReadReq.Types.Options.Types.StreamOptions
				{
					StreamIdentifier = new StreamIdentifier
					{
						StreamName = ByteString.CopyFromUtf8(streamId)
					},
					End = new Empty()
				},
				ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Backwards,
				ResolveLinks = true,
				Count = count,
				NoFilter = new Empty(),
				UuidOption = new ReadReq.Types.Options.Types.UUIDOption
				{
					Structured = new Empty()
				},
				ControlOption = new ReadReq.Types.Options.Types.ControlOption
				{
					Compatibility = 21
				}
			}
		}, GetCallOptions(_operationCancellationToken));

		var events = new List<ReadResp.Types.ReadEvent>();
		while (await call.ResponseStream.MoveNext(_operationCancellationToken))
		{
			if (call.ResponseStream.Current.ContentCase == ReadResp.ContentOneofCase.Event)
			{
				events.Add(call.ResponseStream.Current.Event);
			}
		}

		return events;
	}

	private async Task WaitForProjectionStatus(string name, Func<string, bool> predicate)
	{
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_operationCancellationToken);
		cancellation.CancelAfter(PollTimeout);
		var cancellationToken = cancellation.Token;
		string lastStatus = null;
		try
		{
			for (var attempt = 0; attempt < PollAttemptCount; attempt++)
			{
				var statistics = await ProjectionClient.Statistics(new StatisticsReq.Types.Options
				{
					Name = name
				}, cancellationToken);
				lastStatus = statistics.SingleOrDefault()?.Status;
				if (lastStatus != null && predicate(lastStatus))
				{
					return;
				}

				await Task.Delay(PollInterval, cancellationToken);
			}
		}
		catch (OperationCanceledException) when (!_operationCancellationToken.IsCancellationRequested)
		{
			Assert.Fail($"Projection '{name}' did not reach the expected status. Last status: '{lastStatus}'.");
		}

		Assert.Fail($"Projection '{name}' did not reach the expected status. Last status: '{lastStatus}'.");
	}

	private async Task RunBoundedOperation(Func<Task> operation)
	{
		using var cancellation = new CancellationTokenSource(OperationTimeout);
		_operationCancellationToken = cancellation.Token;
		try
		{
			await operation().WaitAsync(cancellation.Token);
		}
		finally
		{
			_operationCancellationToken = default;
		}
	}

	private static CallOptions GetCallOptions(CancellationToken cancellationToken = default)
	{
		var credentials = CallCredentials.FromInterceptor((_, metadata) =>
		{
			metadata.Add("authorization",
				$"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes("admin:changeit"))}");
			return Task.CompletedTask;
		});

		return new CallOptions(
			credentials: credentials,
			deadline: DateTime.UtcNow.Add(PollTimeout),
			cancellationToken: cancellationToken);
	}
}

[Explicit]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class vnode_cluster_specification<TLogFormat, TStreamId> : specification_with_standard_projections_runnning<TLogFormat, TStreamId>
{
	[Test, Explicit]
	public async Task vnode_cluster_starts()
	{
		await PostProjection(@"fromStream('$user-admin').when({$any:function(){return {}}}).outputState()");
		await AssertStreamTailAsync("$projections-test-projection-result", "Result:{}");
	}
}
