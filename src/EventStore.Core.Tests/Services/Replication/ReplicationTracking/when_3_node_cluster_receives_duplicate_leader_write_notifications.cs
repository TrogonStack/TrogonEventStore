using EventStore.Core.Messages;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Replication.ReplicationTracking;

[TestFixture]
public class when_3_node_cluster_receives_duplicate_leader_write_notifications : with_clustered_replication_tracking_service
{
	private long _logPosition = 4000;

	protected override int ClusterSize => 3;
	public override void When()
	{
		BecomeLeader();
		WriterCheckpoint.Write(_logPosition);
		WriterCheckpoint.Flush();
		Service.Handle(new ReplicationTrackingMessage.WriterCheckpointFlushed());
		Service.Handle(new ReplicationTrackingMessage.WriterCheckpointFlushed());
		AssertEx.IsOrBecomesTrue(() => Service.IsCurrent());
	}

	[Test]
	public void replicated_to_should_not_be_sent()
	{
		Assert.AreEqual(0, ReplicatedTos.Count);
	}
	[Test]
	public void replication_checkpoint_should_not_advance()
	{
		Assert.AreEqual(0, ReplicationCheckpoint.Read());
		Assert.AreEqual(0, ReplicationCheckpoint.ReadNonFlushed());
	}
}
