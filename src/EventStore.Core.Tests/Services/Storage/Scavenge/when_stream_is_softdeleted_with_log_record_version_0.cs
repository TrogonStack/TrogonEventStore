using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Tests.TransactionLog.Scavenging.Helpers;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.LogCommon;
using NUnit.Framework;
using ReadStreamResult = EventStore.Core.Services.Storage.ReaderIndex.ReadStreamResult;

namespace EventStore.Core.Tests.Services.Storage.Scavenge;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint), Ignore = "No such thing as a V0 prepare in LogV3")]
public class
	WhenStreamIsSoftdeletedWithLogRecordVersion0<TLogFormat, TStreamId> : ScavengeTestScenario<TLogFormat,
	TStreamId>
{
	protected override ValueTask<DbResult> CreateDb(TFChunkDbCreationHelper<TLogFormat, TStreamId> dbCreator, CancellationToken token)
	{
		return dbCreator.Chunk(
				Rec.Prepare(0, "$$test", metadata: new StreamMetadata(tempStream: true),
					version: LogRecordVersion.LogRecordV0),
				Rec.Commit(0, "$$test", version: LogRecordVersion.LogRecordV0),
				Rec.Prepare(1, "test", version: LogRecordVersion.LogRecordV0),
				Rec.Commit(1, "test", version: LogRecordVersion.LogRecordV0),
				Rec.Prepare(2, "test", version: LogRecordVersion.LogRecordV0),
				Rec.Commit(2, "test", version: LogRecordVersion.LogRecordV0),
				Rec.Prepare(3, "$$test",
					metadata: new StreamMetadata(truncateBefore: int.MaxValue, tempStream: true),
					version: LogRecordVersion.LogRecordV0),
				Rec.Commit(3, "$$test", version: LogRecordVersion.LogRecordV0))
			.CompleteLastChunk()
			.CreateDb(token: token);
	}

	protected override ILogRecord[][] KeptRecords(DbResult dbResult)
	{
		return new[] { new ILogRecord[0] };
	}

	[Test]
	public async Task scavenging_goes_as_expected()
	{
		await CheckRecords();
	}

	[Test]
	public void the_stream_is_absent_logically()
	{
		Assert.AreEqual(ReadEventResult.NoStream, ReadIndex.ReadEvent("test", 0).Result);
		Assert.AreEqual(ReadStreamResult.NoStream, ReadIndex.ReadStreamEventsForward("test", 0, 100).Result);
		Assert.AreEqual(ReadStreamResult.NoStream, ReadIndex.ReadStreamEventsBackward("test", -1, 100).Result);
	}

	[Test]
	public void the_metastream_is_absent_logically()
	{
		Assert.AreEqual(ReadEventResult.NotFound, ReadIndex.ReadEvent("$$test", 0).Result);
		Assert.AreEqual(ReadStreamResult.Success, ReadIndex.ReadStreamEventsForward("$$test", 0, 100).Result);
		Assert.AreEqual(ReadStreamResult.Success, ReadIndex.ReadStreamEventsBackward("$$test", -1, 100).Result);
	}

	[Test]
	public async Task the_stream_is_absent_physically()
	{
		var headOfTf = new TFPos(Db.Config.WriterCheckpoint.Read(), Db.Config.WriterCheckpoint.Read());
		Assert.IsEmpty(ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 1000).Records
			.Where(x => x.Event.EventStreamId == "test"));
		Assert.IsEmpty((await ReadIndex.ReadAllEventsBackward(headOfTf, 1000, CancellationToken.None)).Records
			.Where(x => x.Event.EventStreamId == "test"));
	}

	[Test]
	public async Task the_metastream_is_absent_physically()
	{
		var headOfTf = new TFPos(Db.Config.WriterCheckpoint.Read(), Db.Config.WriterCheckpoint.Read());
		Assert.IsEmpty(ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 1000).Records
			.Where(x => x.Event.EventStreamId == "$$test"));
		Assert.IsEmpty((await ReadIndex.ReadAllEventsBackward(headOfTf, 1000, CancellationToken.None)).Records
			.Where(x => x.Event.EventStreamId == "$$test"));
	}
}
