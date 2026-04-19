using System;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Integration;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class when_restarting_one_node_at_a_time<TLogFormat, TStreamId> : specification_with_cluster<TLogFormat, TStreamId>
{
	private static readonly TimeSpan RestartTimeout = TimeSpan.FromMinutes(3);
	protected override TimeSpan GivenTimeout { get; } = TimeSpan.FromMinutes(10);

	protected override async Task Given()
	{
		await base.Given();

		for (int i = 0; i < 9; i++)
		{
			var restartedNodeIndex = i % 3;

			AssertEx.IsOrBecomesTrue(
				() => {
					var states = _nodes.Select(x => x.NodeState).ToArray();
					return states.Count(x => x == VNodeState.Leader) == 1 &&
					       states.Count(x => x == VNodeState.Follower) == 2;
				},
				RestartTimeout,
				$"Cluster did not stabilize before restarting node {restartedNodeIndex}",
				MiniNodeLogging.WriteLogs);

			await _nodes[restartedNodeIndex].Shutdown(keepDb: true);
			AssertEx.IsOrBecomesTrue(
				() => {
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

			var node = CreateNode(restartedNodeIndex, _nodeEndpoints[restartedNodeIndex],
				new[] { _nodeEndpoints[(i + 1) % 3].HttpEndPoint, _nodeEndpoints[(i + 2) % 3].HttpEndPoint });
			node.Start();
			_nodes[restartedNodeIndex] = node;

			await Task.WhenAll(_nodes.Select(x => x.Started))
				.WithTimeout(RestartTimeout, MiniNodeLogging.WriteLogs);
			AssertEx.IsOrBecomesTrue(
				() => {
					var states = _nodes.Select(x => x.NodeState).ToArray();
					return states.Count(x => x == VNodeState.Leader) == 1 &&
					       states.Count(x => x == VNodeState.Follower) == 2;
				},
				RestartTimeout,
				$"Cluster did not stabilize after restarting node {restartedNodeIndex}",
				MiniNodeLogging.WriteLogs);
		}
	}

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
