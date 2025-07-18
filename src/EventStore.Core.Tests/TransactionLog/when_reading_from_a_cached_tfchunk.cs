using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Core.Transforms.Identity;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class when_reading_from_a_cached_tfchunk<TLogFormat, TStreamId> : SpecificationWithFilePerTestFixture
{
	private TFChunk _chunk;
	private readonly Guid _corrId = Guid.NewGuid();
	private readonly Guid _eventId = Guid.NewGuid();
	private TFChunk _cachedChunk;
	private IPrepareLogRecord<TStreamId> _record;
	private RecordWriteResult _result;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		var recordFactory = LogFormatHelper<TLogFormat, TStreamId>.RecordFactory;
		var streamId = LogFormatHelper<TLogFormat, TStreamId>.StreamId;
		var eventTypeId = LogFormatHelper<TLogFormat, TStreamId>.EventTypeId;

		_record = LogRecord.Prepare(recordFactory, 0, _corrId, _eventId, 0, 0, streamId, 1,
			PrepareFlags.None, eventTypeId, new byte[12], new byte[15], new DateTime(2000, 1, 1, 12, 0, 0));
		_chunk = await TFChunkHelper.CreateNewChunk(Filename);
		_result = _chunk.TryAppend(_record);
		_chunk.Flush();
		_chunk.Complete();
		_cachedChunk = await TFChunk.FromCompletedFile(Filename, verifyHash: true, unbufferedRead: false,
			reduceFileCachePressure: false, tracker: new TFChunkTracker.NoOp(),
			getTransformFactory: _ => new IdentityChunkTransformFactory());
		await _cachedChunk.CacheInMemory(CancellationToken.None);
	}

	[OneTimeTearDown]
	public override void TestFixtureTearDown()
	{
		_chunk.Dispose();
		_cachedChunk.Dispose();
		base.TestFixtureTearDown();
	}

	[Test]
	public void the_write_result_is_correct()
	{
		Assert.IsTrue(_result.Success);
		Assert.AreEqual(0, _result.OldPosition);
		Assert.AreEqual(_record.GetSizeWithLengthPrefixAndSuffix(), _result.NewPosition);
	}

	[Test]
	public void the_chunk_is_cached()
	{
		Assert.IsTrue(_cachedChunk.IsCached);
	}

	[Test]
	public void the_record_can_be_read_at_exact_position()
	{
		var res = _cachedChunk.TryReadAt(0, couldBeScavenged: true);
		Assert.IsTrue(res.Success);
		Assert.AreEqual(_record, res.LogRecord);
		Assert.AreEqual(_result.OldPosition, res.LogRecord.LogPosition);
	}

	[Test]
	public async Task the_record_can_be_read_as_first_record()
	{
		var res = await _cachedChunk.TryReadFirst(CancellationToken.None);
		Assert.IsTrue(res.Success);
		Assert.AreEqual(_record.GetSizeWithLengthPrefixAndSuffix(), res.NextPosition);
		Assert.AreEqual(_record, res.LogRecord);
		Assert.AreEqual(_result.OldPosition, res.LogRecord.LogPosition);
	}

	[Test]
	public void the_record_can_be_read_as_closest_forward_to_zero_pos()
	{
		var res = _cachedChunk.TryReadClosestForward(0);
		Assert.IsTrue(res.Success);
		Assert.AreEqual(_record.GetSizeWithLengthPrefixAndSuffix(), res.NextPosition);
		Assert.AreEqual(_record, res.LogRecord);
		Assert.AreEqual(_result.OldPosition, res.LogRecord.LogPosition);
	}

	[Test]
	public async Task the_record_can_be_read_as_closest_backward_from_end()
	{
		var res = await _cachedChunk.TryReadClosestBackward(_record.GetSizeWithLengthPrefixAndSuffix(),
			CancellationToken.None);
		Assert.IsTrue(res.Success);
		Assert.AreEqual(0, res.NextPosition);
		Assert.AreEqual(_record, res.LogRecord);
	}

	[Test]
	public async Task the_record_can_be_read_as_last()
	{
		var res = await _cachedChunk.TryReadLast(CancellationToken.None);
		Assert.IsTrue(res.Success);
		Assert.AreEqual(0, res.NextPosition);
		Assert.AreEqual(_record, res.LogRecord);
	}
}
