using System.Linq;
using EventStore.Core.Messages;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Replication.LeaderReplication;

[TestFixture]
public class WhenReplicationServiceRecievesLeaderLogCommitedTo : WithReplicationService
{
	private long _logPosition;

	public override void When()
	{
		_logPosition = 4000;
		Service.Handle(new ReplicationTrackingMessage.ReplicatedTo(_logPosition));
	}

	[Test]
	public void replicated_to_should_be_sent_to_subscriptions()
	{
		var sessions = new[] { ReplicaSession1, ReplicaSession2, ReadOnlyReplicaSession, ReplicaSessionV0 };
		AssertEx.IsOrBecomesTrue(
			() => sessions.Sum(session =>
				session.Messages.Count(message => message is ReplicationTrackingMessage.ReplicatedTo)) == 8,
			msg: "ReplicatedTo messages not received");
		Assert.Multiple(() =>
		{
			Assert.AreEqual(2, ReplicaSession1.Messages.Count(message => message is ReplicationTrackingMessage.ReplicatedTo));
			Assert.AreEqual(2, ReplicaSession2.Messages.Count(message => message is ReplicationTrackingMessage.ReplicatedTo));
			Assert.AreEqual(2, ReadOnlyReplicaSession.Messages.Count(message => message is ReplicationTrackingMessage.ReplicatedTo));
			Assert.AreEqual(2, ReplicaSessionV0.Messages.Count(message => message is ReplicationTrackingMessage.ReplicatedTo));
		});
	}
}
