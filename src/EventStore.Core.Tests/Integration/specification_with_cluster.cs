using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Tests.Helpers;
using EventStore.Plugins.Subsystems;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace EventStore.Core.Tests.Integration;

public abstract class specification_with_cluster<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	protected MiniClusterNode<TLogFormat, TStreamId>[] _nodes;
	protected Endpoints[] _nodeEndpoints;
	protected virtual TimeSpan GivenTimeout { get; } = TimeSpan.FromMinutes(2);
	protected virtual int NodeCount => 3;

	private readonly Dictionary<int, Func<bool, MiniClusterNode<TLogFormat, TStreamId>>> _nodeCreationFactory = new();

	protected class Endpoints
	{
		public readonly IPEndPoint NodeEndPoint;
		public readonly IPEndPoint ReplicationEndPoint;

		public IEnumerable<int> Ports()
		{
			yield return NodeEndPoint.Port;
			yield return ReplicationEndPoint.Port;
		}

		private readonly List<Socket> _sockets;

		public Endpoints()
		{
			_sockets = new List<Socket>();

			var defaultLoopBack = new IPEndPoint(IPAddress.Loopback, 0);

			var nodeEndpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			nodeEndpoint.Bind(defaultLoopBack);
			_sockets.Add(nodeEndpoint);
			var replicationEndpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			replicationEndpoint.Bind(defaultLoopBack);
			_sockets.Add(replicationEndpoint);

			NodeEndPoint = CopyEndpoint((IPEndPoint)nodeEndpoint.LocalEndPoint);
			ReplicationEndPoint = CopyEndpoint((IPEndPoint)replicationEndpoint.LocalEndPoint);
		}

		public void DisposeSockets()
		{
			foreach (var socket in _sockets)
			{
				socket.Dispose();
			}
		}

		private static IPEndPoint CopyEndpoint(IPEndPoint endpoint) =>
			new(endpoint.Address, endpoint.Port);
	}

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();

		MiniNodeLogging.Setup();

		_nodes = new MiniClusterNode<TLogFormat, TStreamId>[NodeCount];
		_nodeEndpoints = Enumerable.Range(0, NodeCount).Select(_ => new Endpoints()).ToArray();
		foreach (var endpoints in _nodeEndpoints)
		{
			endpoints.DisposeSockets();
		}

		var duplicates = _nodeEndpoints.SelectMany(x => x.Ports())
			.GroupBy(x => x)
			.Where(g => g.Count() > 1)
			.Select(x => x.Key)
			.ToList();

		Assert.IsEmpty(duplicates);

		for (var index = 0; index < NodeCount; index++)
		{
			var nodeIndex = index;
			_nodeCreationFactory.Add(nodeIndex, wait => CreateNode(
				nodeIndex,
				_nodeEndpoints[nodeIndex],
				_nodeEndpoints.Where((_, otherIndex) => otherIndex != nodeIndex)
					.Select(x => (EndPoint)x.NodeEndPoint)
					.ToArray(),
				wait));
			_nodes[nodeIndex] = _nodeCreationFactory[nodeIndex](true);
		}

		BeforeNodesStart();

		foreach (var node in _nodes)
		{
			node.Start();
		}

		try
		{
			await Task.WhenAll(_nodes.Select(x => x.Started)).WithTimeout(TimeSpan.FromSeconds(60));
		}
		catch (TimeoutException ex)
		{
			if (_nodes.Count(x => x.Started.IsCompletedSuccessfully) < 2)
			{
				MiniNodeLogging.WriteLogs();
				throw new TimeoutException(
					$"Cluster nodes did not start. Statuses: {string.Join('/', _nodes.Select(x => x.NodeState))}", ex);
			}
		}

		// wait for cluster to be fully operational, tests depend on leader and followers
		AssertEx.IsOrBecomesTrue(() => _nodes.Any(x => x.NodeState == Data.VNodeState.Leader),
			timeout: TimeSpan.FromSeconds(30),
			onFail: MiniNodeLogging.WriteLogs,
			msg: "Waiting for leader timed out!");

		await GetLeader().AdminUserCreated.WithTimeout(TimeSpan.FromMinutes(2), onFail: MiniNodeLogging.WriteLogs);

		//flaky: most tests only need 1 follower, waiting for 2 causes timeouts
		AssertEx.IsOrBecomesTrue(() =>
				_nodes.Any(x => x.NodeState is VNodeState.Follower or VNodeState.ReadOnlyReplica),
			timeout: TimeSpan.FromSeconds(90),
			onFail: MiniNodeLogging.WriteLogs,
			msg: $"Waiting for followers timed out! States={string.Join(", ", _nodes.Select(n => n.NodeState))}");

		try
		{
			await Given().WithTimeout(GivenTimeout);
		}
		catch
		{
			MiniNodeLogging.WriteLogs();
			throw;
		}
	}

	protected virtual void BeforeNodesStart()
	{
	}

	protected virtual Task Given() => Task.CompletedTask;

	protected Task ShutdownNode(int nodeNum) => _nodes[nodeNum].Shutdown(keepDb: true);

	protected virtual MiniClusterNode<TLogFormat, TStreamId> CreateNode(int index, Endpoints endpoints, EndPoint[] gossipSeeds,
		bool wait = true) => new(
		PathName, index, endpoints.NodeEndPoint, endpoints.ReplicationEndPoint,
		subsystems: Array.Empty<ISubsystem>(), gossipSeeds: gossipSeeds);

	[TearDown]
	public void AfterEachTest()
	{
		if (TestContext.CurrentContext.Result.Outcome.Status is TestStatus.Failed)
		{
			MiniNodeLogging.WriteLogs();
		}
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		if (_nodes is not null)
		{
			await Task.WhenAll(_nodes.Where(node => node is not null).Select(node => node.Shutdown()));
		}

		MiniNodeLogging.Clear();

		await base.TestFixtureTearDown();
	}

	protected static void WaitIdle()
	{
	}

	protected MiniClusterNode<TLogFormat, TStreamId> GetLeader()
	{
		var leader = _nodes.First(x => x.NodeState == Data.VNodeState.Leader);
		Assert.NotNull(leader, "Cluster doesn't have a leader available!");

		return leader;
	}

	protected MiniClusterNode<TLogFormat, TStreamId>[] GetFollowers()
	{
		var followers = _nodes.Where(x => x.NodeState == Data.VNodeState.Follower).ToArray();
		Assert.IsNotEmpty(followers, "Cluster doesn't have followers available!");

		return followers;
	}
}
