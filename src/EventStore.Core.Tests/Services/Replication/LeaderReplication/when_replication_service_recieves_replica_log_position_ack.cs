using EventStore.Core.Messages;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Replication.LeaderReplication;

[TestFixture]
public class WhenReplicationServiceReceivesReplicaLogPositionAckSubscriptionV0 : WithReplicationService
{
	private long _replicationLogPosition;
	private long _writerLogPosition;

	public override void When()
	{
		_replicationLogPosition = 4000;
		_writerLogPosition = 3000;
		Service.Handle(new ReplicationMessage.ReplicaLogPositionAck(ReplicaIdV0, _replicationLogPosition, _writerLogPosition));
	}

	[Test]
	public void replica_Log_written_to_should_be_published()
	{
		AssertEx.IsOrBecomesTrue(() => ReplicaWriteAcks.Count == 1, msg: "ReplicaLogWrittenTo msg not received");
		Assert.True(ReplicaWriteAcks.TryDequeue(out var commit));

		Assert.AreEqual(ReplicaIdV0, commit.SubscriptionId);
		Assert.AreEqual(_replicationLogPosition, commit.ReplicationLogPosition);
	}
}

[TestFixture]
public class WhenReplicationServiceReceivesReplicaLogPositionAckSubscriptionV1 : WithReplicationService
{
	private long _replicationLogPosition;
	private long _writerLogPosition;

	public override void When()
	{
		_replicationLogPosition = 4000;
		_writerLogPosition = 3000;
		Service.Handle(
			new ReplicationMessage.ReplicaLogPositionAck(ReplicaId, _replicationLogPosition, _writerLogPosition));
	}

	[Test]
	public void replica_Log_written_to_should_be_published()
	{
		AssertEx.IsOrBecomesTrue(() => ReplicaWriteAcks.Count == 1, msg: "ReplicaLogWrittenTo msg not received");
		Assert.True(ReplicaWriteAcks.TryDequeue(out var commit));

		Assert.AreEqual(ReplicaId, commit.SubscriptionId);
		Assert.AreEqual(_writerLogPosition, commit.ReplicationLogPosition);
	}
}
