using System;
using System.Threading.Tasks;
using EventStore.Core.Messages;
using EventStore.Core.TransactionLog.Checkpoint;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.IndexCommitter;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class when_index_committer_service_commits_empty_transaction_at_end_of_log<TLogFormat, TStreamId> : with_index_committer_service<TLogFormat, TStreamId>
{
	private readonly Guid _correlationId = Guid.NewGuid();
	private readonly long _logPrePosition = 4000;
	private readonly long _logPostPosition = 4001;

	public override Task TestFixtureSetUp()
	{
		ReplicationCheckpoint = new InMemoryCheckpoint(0);
		return base.TestFixtureSetUp();
	}

	public override void Given() { }

	public override void When()
	{
		WriterCheckpoint.Write(_logPostPosition);
		WriterCheckpoint.Flush();

		Service.AddPendingPrepare(_logPrePosition, [], _logPostPosition);
		Service.Handle(new StorageMessage.CommitChased(_correlationId, _logPrePosition, _logPrePosition, 0, 0));

		ReplicationCheckpoint.Write(_logPostPosition);
		ReplicationCheckpoint.Flush();
		Service.Handle(new ReplicationTrackingMessage.ReplicatedTo(_logPostPosition));
	}

	[Test]
	public void commit_replicated_message_should_have_been_published()
	{
		AssertEx.IsOrBecomesTrue(() => 1 == CommitReplicatedMgs.Count);
	}

	[Test]
	public void index_written_message_should_have_been_published()
	{
		AssertEx.IsOrBecomesTrue(() => 1 == IndexWrittenMgs.Count);
	}

	[Test]
	public void tf_eof_notification_should_have_been_published()
	{
		AssertEx.IsOrBecomesTrue(() => 1 == TfEofAtNonCommitRecordMgs.Count);
	}
}
