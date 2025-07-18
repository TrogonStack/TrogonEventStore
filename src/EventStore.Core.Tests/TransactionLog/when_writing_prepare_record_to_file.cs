using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Tests.TransactionLog;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.LogCommon;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class when_writing_prepare_record_to_file<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	private ITransactionFileWriter _writer;
	private InMemoryCheckpoint _writerCheckpoint;
	private readonly Guid _eventId = Guid.NewGuid();
	private readonly Guid _correlationId = Guid.NewGuid();
	private IPrepareLogRecord<TStreamId> _record;
	private TFChunkDb _db;

	[OneTimeSetUp]
	public async Task SetUp()
	{
		_writerCheckpoint = new InMemoryCheckpoint();
		_db = new TFChunkDb(TFChunkHelper.CreateDbConfig(PathName, _writerCheckpoint, new InMemoryCheckpoint(),
			1024));
		await _db.Open();
		_writer = new TFChunkWriter(_db);
		_writer.Open();

		var recordFactory = LogFormatHelper<TLogFormat, TStreamId>.RecordFactory;
		var streamId = LogFormatHelper<TLogFormat, TStreamId>.StreamId;
		var eventTypeId = LogFormatHelper<TLogFormat, TStreamId>.EventTypeId;

		_record = LogRecord.Prepare(
			factory: recordFactory,
			logPosition: 0,
			eventId: _eventId,
			correlationId: _correlationId,
			transactionPos: 0xDEAD,
			transactionOffset: 0xBEEF,
			eventStreamId: streamId,
			expectedVersion: 1234,
			timeStamp: new DateTime(2012, 12, 21),
			flags: PrepareFlags.SingleWrite,
			eventType: eventTypeId,
			data: new byte[] { 1, 2, 3, 4, 5 },
			metadata: new byte[] { 7, 17 });
		await _writer.Write(_record, CancellationToken.None);
		_writer.Flush();
	}

	[OneTimeTearDown]
	public async Task Teardown()
	{
		_writer.Close();
		await _db.DisposeAsync();
	}

	[Test]
	public void the_data_is_written()
	{
		//TODO MAKE THIS ACTUALLY ASSERT OFF THE FILE AND READER FROM KNOWN FILE
		using (var reader = new TFChunkChaser(_db, _writerCheckpoint, _db.Config.ChaserCheckpoint, false))
		{
			reader.Open();
			ILogRecord r;
			Assert.IsTrue(reader.TryReadNext(out r));

			var streamId = LogFormatHelper<TLogFormat, TStreamId>.StreamId;
			var eventTypeId = LogFormatHelper<TLogFormat, TStreamId>.EventTypeId;

			Assert.True(r is IPrepareLogRecord<TStreamId>);
			var p = (IPrepareLogRecord<TStreamId>)r;
			Assert.AreEqual(p.RecordType, LogRecordType.Prepare);
			Assert.AreEqual(p.LogPosition, 0);
			Assert.AreEqual(p.TransactionPosition, 0xDEAD);
			Assert.AreEqual(p.TransactionOffset, 0xBEEF);
			Assert.AreEqual(p.CorrelationId, _correlationId);
			Assert.AreEqual(p.EventId, _eventId);
			Assert.AreEqual(p.EventStreamId, streamId);
			Assert.AreEqual(p.ExpectedVersion, 1234);
			Assert.That(p.TimeStamp, Is.EqualTo(new DateTime(2012, 12, 21)).Within(7).Milliseconds);
			Assert.AreEqual(p.Flags, PrepareFlags.SingleWrite);
			Assert.AreEqual(p.EventType, eventTypeId);
			Assert.AreEqual(p.Data.Length, 5);
			Assert.AreEqual(p.Metadata.Length, 2);
		}
	}

	[Test]
	public void the_checksum_is_updated()
	{
		Assert.AreEqual(_record.GetSizeWithLengthPrefixAndSuffix(), _writerCheckpoint.Read());
	}

	[Test]
	public void trying_to_read_past_writer_checksum_returns_false()
	{
		var reader = new TFChunkReader(_db, _writerCheckpoint);
		Assert.IsFalse(reader.TryReadAt(_writerCheckpoint.Read(), couldBeScavenged: true).Success);
	}
}
