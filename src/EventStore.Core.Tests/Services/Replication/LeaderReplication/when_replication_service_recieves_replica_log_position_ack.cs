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
		ReplicaSession1.SetSentReplicationPosition(_replicationLogPosition);
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

[TestFixture]
public class WhenReplicationServiceReceivesNegativeReplicaLogPositionAck : WithReplicationService
{
	public override void When() =>
		Service.Handle(new ReplicationMessage.ReplicaLogPositionAck(ReplicaId, -1, 0));

	[Test]
	public void acknowledgement_is_rejected_before_quorum_publication()
	{
		Assert.That(ReplicaWriteAcks, Is.Empty);
		Assert.That(ReplicaSession1.IsClosed, Is.True);
	}
}

[TestFixture]
public class WhenReplicationServiceReceivesWriterAheadReplicaLogPositionAck : WithReplicationService
{
	public override void When() =>
		Service.Handle(new ReplicationMessage.ReplicaLogPositionAck(ReplicaId, 0, 1));

	[Test]
	public void acknowledgement_is_rejected_before_quorum_publication()
	{
		Assert.That(ReplicaWriteAcks, Is.Empty);
		Assert.That(ReplicaSession1.IsClosed, Is.True);
	}
}

[TestFixture]
public class WhenReplicationServiceReceivesUnsentReplicaLogPositionAck : WithReplicationService
{
	public override void When()
	{
		ReplicaSession1.SetSentReplicationPosition(0);
		Service.Handle(new ReplicationMessage.ReplicaLogPositionAck(ReplicaId, 1, 0));
	}

	[Test]
	public void acknowledgement_is_rejected_before_quorum_publication()
	{
		Assert.That(ReplicaWriteAcks, Is.Empty);
		Assert.That(ReplicaSession1.IsClosed, Is.True);
	}
}

[TestFixture]
public class WhenReplicationServiceReceivesRegressingReplicaLogPositionAck : WithReplicationService
{
	public override void When()
	{
		ReplicaSession1.SetSentReplicationPosition(100);
		Service.Handle(new ReplicationMessage.ReplicaLogPositionAck(ReplicaId, 100, 90));
		Service.Handle(new ReplicationMessage.ReplicaLogPositionAck(ReplicaId, 99, 89));
	}

	[Test]
	public void regressing_acknowledgement_is_rejected_before_another_quorum_publication()
	{
		Assert.That(ReplicaWriteAcks, Has.Exactly(1).Items);
		Assert.That(ReplicaSession1.IsClosed, Is.True);
	}
}
