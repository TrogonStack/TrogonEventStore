using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Tests.Integration;
using NUnit.Framework;

namespace EventStore.Core.Tests.Replication.ReadStream;

[Category("LongRunning")]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class when_subscribed_to_stream_on_leader_and_event_is_replicated_to_followers<TLogFormat, TStreamId> : specification_with_cluster<TLogFormat, TStreamId> {
	private const string _streamId = "test-stream";
	private static readonly TimeSpan _topologyTimeout = TimeSpan.FromSeconds(90);
	private static readonly TimeSpan _subscriptionTimeout = TimeSpan.FromSeconds(30);
	private CountdownEvent _subscriptionsConfirmed;
	private TestSubscription<TLogFormat, TStreamId> _leaderSubscription;
	private List<TestSubscription<TLogFormat, TStreamId>> _followerSubscriptions;

	protected override async Task Given() {
		AssertEx.IsOrBecomesTrue(
			() => {
				var states = _nodes.Select(x => x.NodeState).ToArray();
				return states.Count(x => x == VNodeState.Leader) == 1 &&
					   states.Count(x => x == VNodeState.Follower) == 2;
			},
			_topologyTimeout,
			"Timed out waiting for one leader and two followers",
			MiniNodeLogging.WriteLogs);

		var leader = GetLeader();
		Assert.IsNotNull(leader, "Could not get leader node");

		// Set the checkpoint so the check is not skipped
		leader.Db.Config.ReplicationCheckpoint.Write(0);

		var followers = GetFollowers();
		Assert.AreEqual(2, followers.Length, "Expected two follower nodes");

		_subscriptionsConfirmed = new CountdownEvent(1 + followers.Length);
		_leaderSubscription = new TestSubscription<TLogFormat, TStreamId>(leader, 1, _streamId, _subscriptionsConfirmed);
		_leaderSubscription.CreateSubscription();

		_followerSubscriptions = new List<TestSubscription<TLogFormat, TStreamId>>();
		foreach (var s in followers) {
			var followerSubscription = new TestSubscription<TLogFormat, TStreamId>(s, 1, _streamId, _subscriptionsConfirmed);
			_followerSubscriptions.Add(followerSubscription);
			followerSubscription.CreateSubscription();
		}

		if (!_subscriptionsConfirmed.Wait(_subscriptionTimeout)) {
			Assert.Fail($"Timed out waiting for subscriptions to confirm, confirmed {_subscriptionsConfirmed.CurrentCount} need {_subscriptionsConfirmed.InitialCount}.");
		}

		var events = new Event[] { new Event(Guid.NewGuid(), "test-type", false, new byte[10], new byte[0]) };
		var writeResult = ReplicationTestHelper.WriteEvent(leader, events, _streamId);
		Assert.AreEqual(OperationResult.Success, writeResult.Result);

		await base.Given();
		var replicas = GetFollowers();
		AssertEx.IsOrBecomesTrue(
			() => {
				var leaderIndex = leader.Db.Config.IndexCheckpoint.Read();
				return replicas[0].Db.Config.IndexCheckpoint.Read() == leaderIndex &&
					   replicas[1].Db.Config.IndexCheckpoint.Read() == leaderIndex;

			},
			timeout: TimeSpan.FromSeconds(2));
	}

	[Test]
	public void should_receive_event_on_leader() {
		Assert.IsTrue(_leaderSubscription.EventAppeared.Wait(2000));
	}

	[Test]
	public void should_receive_event_on_followers() {
		if (!(_followerSubscriptions[0].EventAppeared.Wait(2000) && _followerSubscriptions[1].EventAppeared.Wait(2000))) {
			Assert.Fail("Timed out waiting for follower subscriptions to get events");
		}
	}
}
