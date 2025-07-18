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
public class WhenStreamIsSoftdeletedWithMixedLogRecordVersion0AndVersion1<TLogFormat, TStreamId> : ScavengeTestScenario<TLogFormat, TStreamId>
{
	private const string _deletedStream = "test";
	private const string _deletedMetaStream = "$$test";
	private const string _keptStream = "other";

	protected override ValueTask<DbResult> CreateDb(TFChunkDbCreationHelper<TLogFormat, TStreamId> dbCreator, CancellationToken token)
	{
		return dbCreator.Chunk(
				Rec.Prepare(0, _deletedMetaStream, metadata: new StreamMetadata(tempStream: true),
					version: LogRecordVersion.LogRecordV0),
				Rec.Commit(0, _deletedMetaStream, version: LogRecordVersion.LogRecordV0),
				Rec.Prepare(1, _keptStream, version: LogRecordVersion.LogRecordV1),
				Rec.Commit(1, _keptStream, version: LogRecordVersion.LogRecordV1),
				Rec.Prepare(2, _keptStream, version: LogRecordVersion.LogRecordV0),
				Rec.Commit(2, _keptStream, version: LogRecordVersion.LogRecordV0),
				Rec.Prepare(3, _deletedStream, version: LogRecordVersion.LogRecordV1),
				Rec.Commit(3, _deletedStream, version: LogRecordVersion.LogRecordV0),
				Rec.Prepare(4, _deletedStream, version: LogRecordVersion.LogRecordV0),
				Rec.Commit(4, _deletedStream, version: LogRecordVersion.LogRecordV1),
				Rec.Prepare(5, _keptStream, version: LogRecordVersion.LogRecordV1),
				Rec.Commit(5, _keptStream, version: LogRecordVersion.LogRecordV1),
				Rec.Prepare(6, _deletedMetaStream,
					metadata: new StreamMetadata(truncateBefore: EventNumber.DeletedStream, tempStream: true),
					version: LogRecordVersion.LogRecordV1),
				Rec.Commit(6, _deletedMetaStream, version: LogRecordVersion.LogRecordV0))
			.CompleteLastChunk()
			.CreateDb(token: token);
	}

	protected override ILogRecord[][] KeptRecords(DbResult dbResult)
	{
		return new[] {
			new[] {
				dbResult.Recs[0][2],
				dbResult.Recs[0][3],
				dbResult.Recs[0][4],
				dbResult.Recs[0][5],
				dbResult.Recs[0][10],
				dbResult.Recs[0][11]
			}
		};
	}

	[Test]
	public async Task scavenging_goes_as_expected()
	{
		await CheckRecords();
	}

	[Test]
	public void the_stream_is_absent_logically()
	{
		Assert.AreEqual(ReadEventResult.NoStream, ReadIndex.ReadEvent(_deletedStream, 0).Result);
		Assert.AreEqual(ReadStreamResult.NoStream,
			ReadIndex.ReadStreamEventsForward(_deletedStream, 0, 100).Result);
		Assert.AreEqual(ReadStreamResult.NoStream,
			ReadIndex.ReadStreamEventsBackward(_deletedStream, -1, 100).Result);
	}

	[Test]
	public void the_metastream_is_absent_logically()
	{
		Assert.AreEqual(ReadEventResult.NotFound, ReadIndex.ReadEvent(_deletedMetaStream, 0).Result);
		Assert.AreEqual(ReadStreamResult.Success,
			ReadIndex.ReadStreamEventsForward(_deletedMetaStream, 0, 100).Result);
		Assert.AreEqual(ReadStreamResult.Success,
			ReadIndex.ReadStreamEventsBackward(_deletedMetaStream, -1, 100).Result);
	}

	[Test]
	public async Task the_stream_is_absent_physically()
	{
		var headOfTf = new TFPos(Db.Config.WriterCheckpoint.Read(), Db.Config.WriterCheckpoint.Read());
		Assert.IsEmpty(ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 1000).Records
			.Where(x => x.Event.EventStreamId == _deletedStream));
		Assert.IsEmpty((await ReadIndex.ReadAllEventsBackward(headOfTf, 1000, CancellationToken.None)).Records
			.Where(x => x.Event.EventStreamId == _deletedStream));
	}

	[Test]
	public async Task the_metastream_is_absent_physically()
	{
		var headOfTf = new TFPos(Db.Config.WriterCheckpoint.Read(), Db.Config.WriterCheckpoint.Read());
		Assert.IsEmpty(ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 1000).Records
			.Where(x => x.Event.EventStreamId == _deletedMetaStream));
		Assert.IsEmpty((await ReadIndex.ReadAllEventsBackward(headOfTf, 1000, CancellationToken.None)).Records
			.Where(x => x.Event.EventStreamId == _deletedMetaStream));
	}

	[Test]
	public void the_kept_stream_is_present()
	{
		Assert.AreEqual(ReadEventResult.Success, ReadIndex.ReadEvent(_keptStream, 0).Result);
		Assert.AreEqual(ReadStreamResult.Success, ReadIndex.ReadStreamEventsForward(_keptStream, 0, 100).Result);
		Assert.AreEqual(ReadStreamResult.Success, ReadIndex.ReadStreamEventsBackward(_keptStream, -1, 100).Result);
	}
}
