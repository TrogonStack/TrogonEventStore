using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.CheckCommitStartingAt;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint), Ignore = "Explicit transactions are not supported yet by Log V3")]
public class
	when_writing_few_prepares_with_same_expected_version_and_committing_one_of_them<TLogFormat, TStreamId> : ReadIndexTestScenario<TLogFormat, TStreamId>
{
	private IPrepareLogRecord _prepare0;
	private IPrepareLogRecord _prepare1;
	private IPrepareLogRecord _prepare2;

	protected override void WriteTestScenario()
	{
		_prepare0 = WritePrepare("ES", expectedVersion: -1);
		_prepare1 = WritePrepare("ES", expectedVersion: -1);
		_prepare2 = WritePrepare("ES", expectedVersion: -1);
		WriteCommit(_prepare1.LogPosition, "ES", eventNumber: 0);
	}

	[Test]
	public void other_prepares_cannot_be_committed()
	{
		var res = ReadIndex.IndexWriter.CheckCommitStartingAt(_prepare0.LogPosition,
			WriterCheckpoint.ReadNonFlushed());

		Assert.AreEqual(CommitDecision.WrongExpectedVersion, res.Decision);
		Assert.AreEqual("ES", res.EventStreamId);
		Assert.AreEqual(0, res.CurrentVersion);
		Assert.AreEqual(-1, res.StartEventNumber);
		Assert.AreEqual(-1, res.EndEventNumber);

		res = ReadIndex.IndexWriter.CheckCommitStartingAt(_prepare2.LogPosition, WriterCheckpoint.ReadNonFlushed());

		Assert.AreEqual(CommitDecision.WrongExpectedVersion, res.Decision);
		Assert.AreEqual("ES", res.EventStreamId);
		Assert.AreEqual(0, res.CurrentVersion);
		Assert.AreEqual(-1, res.StartEventNumber);
		Assert.AreEqual(-1, res.EndEventNumber);
	}
}
