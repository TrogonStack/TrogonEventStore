using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.CheckCommitStartingAt;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class WhenWritingFewPreparesWithSameExpectedVersionAndNotCommittingThem<TLogFormat, TStreamId> : ReadIndexTestScenario<TLogFormat, TStreamId>
{
	private IPrepareLogRecord _prepare0;
	private IPrepareLogRecord _prepare1;
	private IPrepareLogRecord _prepare2;

	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		_prepare0 = await WritePrepare("ES", -1, token: token);
		_prepare1 = await WritePrepare("ES", -1, token: token);
		_prepare2 = await WritePrepare("ES", -1, token: token);
	}

	[Test]
	public void every_prepare_can_be_commited()
	{
		var res = ReadIndex.IndexWriter.CheckCommitStartingAt(_prepare0.LogPosition,
			WriterCheckpoint.ReadNonFlushed());

		var streamId = _logFormat.StreamIds.LookupValue("ES");

		Assert.AreEqual(CommitDecision.Ok, res.Decision);
		Assert.AreEqual(streamId, res.EventStreamId);
		Assert.AreEqual(-1, res.CurrentVersion);
		Assert.AreEqual(-1, res.StartEventNumber);
		Assert.AreEqual(-1, res.EndEventNumber);

		res = ReadIndex.IndexWriter.CheckCommitStartingAt(_prepare1.LogPosition, WriterCheckpoint.ReadNonFlushed());

		Assert.AreEqual(CommitDecision.Ok, res.Decision);
		Assert.AreEqual(streamId, res.EventStreamId);
		Assert.AreEqual(-1, res.CurrentVersion);
		Assert.AreEqual(-1, res.StartEventNumber);
		Assert.AreEqual(-1, res.EndEventNumber);

		res = ReadIndex.IndexWriter.CheckCommitStartingAt(_prepare2.LogPosition, WriterCheckpoint.ReadNonFlushed());

		Assert.AreEqual(CommitDecision.Ok, res.Decision);
		Assert.AreEqual(streamId, res.EventStreamId);
		Assert.AreEqual(-1, res.CurrentVersion);
		Assert.AreEqual(-1, res.StartEventNumber);
		Assert.AreEqual(-1, res.EndEventNumber);
	}
}
