using System;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Integration;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[NonParallelizable]
public class when_restarting_one_node_at_a_time<TLogFormat, TStreamId> : specification_with_cluster<TLogFormat, TStreamId>
{
	private const int RestartCount = 3;
	private static readonly bool IsArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
	private static readonly TimeSpan InitialStabilizationTimeout = TimeSpan.FromMinutes(IsArm64 ? 15 : 5);
	private static readonly TimeSpan RestartTimeout = TimeSpan.FromMinutes(10);
	protected override TimeSpan GivenTimeout { get; } = TimeSpan.FromMinutes(IsArm64 ? 30 : 20);

	protected override async Task Given()
	{
		await base.Given();

		var restartedNodes = new bool[RestartCount];
		for (int i = 0; i < RestartCount; i++)
		{
			AssertEx.IsOrBecomesTrue(
				() =>
				{
					var states = _nodes.Select(x => x.NodeState).ToArray();
					return states.Count(x => x == VNodeState.Leader) == 1 &&
						   states.Count(x => x == VNodeState.Follower) == 2;
				},
				i == 0 ? InitialStabilizationTimeout : RestartTimeout,
				$"Cluster did not stabilize before restart iteration {i + 1}",
				MiniNodeLogging.WriteLogs);

			var restartedNodeIndex = SelectRestartNode(restartedNodes, restartLeader: i == RestartCount - 1);
			restartedNodes[restartedNodeIndex] = true;

			await _nodes[restartedNodeIndex].Shutdown(keepDb: true);
			AssertEx.IsOrBecomesTrue(
				() =>
				{
					var states = _nodes
						.Where((_, index) => index != restartedNodeIndex)
						.Select(x => x.NodeState)
						.ToArray();
					return states.Count(x => x == VNodeState.Leader) == 1 &&
						   states.Count(x => x == VNodeState.Follower) == 1;
				},
				RestartTimeout,
				$"Remaining cluster did not stabilize after shutting down node {restartedNodeIndex}",
				MiniNodeLogging.WriteLogs);

			var node = CreateNode(restartedNodeIndex, _nodeEndpoints[restartedNodeIndex], GossipSeedsFor(restartedNodeIndex));
			node.Start();
			_nodes[restartedNodeIndex] = node;

			AssertEx.IsOrBecomesTrue(
				() =>
				{
					var states = _nodes.Select(x => x.NodeState).ToArray();
					return states.Count(x => x == VNodeState.Leader) == 1 &&
						   states.Count(x => x == VNodeState.Follower) == 2;
				},
				RestartTimeout,
				$"Cluster did not stabilize after restarting node {restartedNodeIndex}",
				MiniNodeLogging.WriteLogs);
		}
	}

	private int SelectRestartNode(bool[] restartedNodes, bool restartLeader)
	{
		var targetState = restartLeader ? VNodeState.Leader : VNodeState.Follower;
		var candidate = _nodes
			.Select((node, index) => (node, index))
			.FirstOrDefault(x => !restartedNodes[x.index] && x.node.NodeState == targetState);

		if (candidate.node is not null)
			return candidate.index;

		var fallbackIndex = Array.FindIndex(restartedNodes, restarted => !restarted);
		if (fallbackIndex >= 0)
			return fallbackIndex;

		throw new InvalidOperationException("All cluster nodes have already been restarted.");
	}

	private EndPoint[] GossipSeedsFor(int restartedNodeIndex) =>
		_nodeEndpoints
			.Where((_, index) => index != restartedNodeIndex)
			.Select(x => (EndPoint)x.HttpEndPoint)
			.ToArray();

	[Test]
	public void cluster_should_stabilize()
	{
		var leaders = 0;
		var followers = 0;
		var acceptedStates = new[] { VNodeState.Leader, VNodeState.Follower };

		for (int i = 0; i < 3; i++)
		{
			AssertEx.IsOrBecomesTrue(() => acceptedStates.Contains(_nodes[i].NodeState),
				TimeSpan.FromSeconds(5), $"node {i} failed to become a leader/follower");

			var state = _nodes[i].NodeState;
			if (state == VNodeState.Leader)
				leaders++;
			else if (state == VNodeState.Follower)
				followers++;
			else
				throw new Exception($"node {i} in unexpected state {state}");
		}

		Assert.AreEqual(1, leaders);
		Assert.AreEqual(2, followers);
	}
}
