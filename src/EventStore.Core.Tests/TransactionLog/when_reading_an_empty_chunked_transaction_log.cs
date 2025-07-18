using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.LogV2;
using EventStore.Core.Tests.TransactionLog;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class WhenReadingAnEmptyChunkedTransactionLog<TLogFormat, TStreamId> : SpecificationWithDirectory
{
	[Test]
	public async Task try_read_returns_false_when_writer_checksum_is_zero()
	{
		var writerchk = new InMemoryCheckpoint(0);
		var chaserchk = new InMemoryCheckpoint(0);
		await using var db = new TFChunkDb(TFChunkHelper.CreateDbConfig(PathName, writerchk, chaserchk));
		await db.Open();

		var reader = new TFChunkReader(db, writerchk, 0);
		Assert.IsFalse(reader.TryReadNext().Success);
	}

	[Test]
	public async Task try_read_does_not_cache_anything_and_returns_record_once_it_is_written_later()
	{
		var writerchk = new InMemoryCheckpoint(0);
		var chaserchk = new InMemoryCheckpoint(0);
		await using var db = new TFChunkDb(TFChunkHelper.CreateDbConfig(PathName, writerchk, chaserchk));
		await db.Open();

		var writer = new TFChunkWriter(db);
		writer.Open();

		var reader = new TFChunkReader(db, writerchk, 0);

		Assert.IsFalse(reader.TryReadNext().Success);

		var recordFactory = LogFormatHelper<TLogFormat, TStreamId>.RecordFactory;
		var streamId = LogFormatHelper<TLogFormat, TStreamId>.StreamId;
		var eventTypeId = LogFormatHelper<TLogFormat, TStreamId>.EventTypeId;
		var rec = LogRecord.SingleWrite(recordFactory, 0, Guid.NewGuid(), Guid.NewGuid(), streamId, -1, eventTypeId,
			new byte[] { 7 }, null);

		Assert.IsTrue(await writer.Write(rec, CancellationToken.None) is (true, _));
		writer.Flush();
		writer.Close();

		var res = reader.TryReadNext();
		Assert.IsTrue(res.Success);
		Assert.AreEqual(rec, res.LogRecord);
	}
}
