using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.LogCommon;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.Scavenge;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint), Ignore = "No such thing as a V0 prepare in LogV3")]
public class when_scavenging_tfchunk_with_version0_log_records_and_deleted_records<TLogFormat, TStreamId> : ReadIndexTestScenario<TLogFormat, TStreamId>
{

	private const string _eventStreamId = "ES";
	private const string _deletedEventStreamId = "Deleted-ES";
	private PrepareLogRecord _event1, _event2, _event3, _event4, _deleted;

	protected override void WriteTestScenario()
	{
		// Stream that will be kept
		_event1 = WriteSingleEventWithLogVersion0(Guid.NewGuid(), _eventStreamId, Writer.Position,
			0);
		_event2 = WriteSingleEventWithLogVersion0(Guid.NewGuid(), _eventStreamId, Writer.Position,
			1);

		// Stream that will be deleted
		WriteSingleEventWithLogVersion0(Guid.NewGuid(), _deletedEventStreamId, Writer.Position,
			0);
		WriteSingleEventWithLogVersion0(Guid.NewGuid(), _deletedEventStreamId, Writer.Position,
			1);
		_deleted = WriteSingleEventWithLogVersion0(Guid.NewGuid(), _deletedEventStreamId,
			Writer.Position, int.MaxValue - 1,
			PrepareFlags.StreamDelete | PrepareFlags.TransactionBegin | PrepareFlags.TransactionEnd);

		// Stream that will be kept
		_event3 = WriteSingleEventWithLogVersion0(Guid.NewGuid(), _eventStreamId, Writer.Position,
			2);
		_event4 = WriteSingleEventWithLogVersion0(Guid.NewGuid(), _eventStreamId, Writer.Position,
			3);

		Writer.CompleteChunk();
		Writer.AddNewChunk();

		Scavenge(completeLast: false, mergeChunks: true);
	}

	[Test]
	public void should_be_able_to_read_the_all_stream()
	{
		var events = ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 100).Records.Select(r => r.Event).ToArray();
		Assert.AreEqual(5, events.Count());
		Assert.AreEqual(_event1.EventId, events[0].EventId);
		Assert.AreEqual(_event2.EventId, events[1].EventId);
		Assert.AreEqual(_deleted.EventId, events[2].EventId);
		Assert.AreEqual(_event3.EventId, events[3].EventId);
		Assert.AreEqual(_event4.EventId, events[4].EventId);
	}

	[Test]
	public async Task should_have_updated_deleted_stream_event_number()
	{
		var chunk = Db.Manager.GetChunk(0);
		var chunkRecords = new List<ILogRecord>();
		RecordReadResult result = await chunk.TryReadFirst(CancellationToken.None);
		while (result.Success)
		{
			chunkRecords.Add(result.LogRecord);
			result = chunk.TryReadClosestForward(result.NextPosition);
		}

		var deletedRecord = (PrepareLogRecord)chunkRecords.First(x => x.RecordType == LogRecordType.Prepare
																	  && ((PrepareLogRecord)x).EventStreamId ==
																	  _deletedEventStreamId);


		Assert.AreEqual(EventNumber.DeletedStream - 1, deletedRecord.ExpectedVersion);
	}

	[Test]
	public async Task the_log_records_are_still_version_0()
	{
		var chunk = Db.Manager.GetChunk(0);
		var chunkRecords = new List<ILogRecord>();
		RecordReadResult result = await chunk.TryReadFirst(CancellationToken.None);
		while (result.Success)
		{
			chunkRecords.Add(result.LogRecord);
			result = chunk.TryReadClosestForward(result.NextPosition);
		}

		Assert.IsTrue(chunkRecords.All(x => x.Version == LogRecordVersion.LogRecordV0));
		Assert.AreEqual(10, chunkRecords.Count);
	}

	[Test]
	public void should_be_able_to_read_the_stream()
	{
		var events = ReadIndex.ReadStreamEventsForward(_eventStreamId, 0, 10);
		Assert.AreEqual(4, events.Records.Length);
		Assert.AreEqual(_event1.EventId, events.Records[0].EventId);
		Assert.AreEqual(_event2.EventId, events.Records[1].EventId);
		Assert.AreEqual(_event3.EventId, events.Records[2].EventId);
		Assert.AreEqual(_event4.EventId, events.Records[3].EventId);
	}

	[Test]
	public void the_deleted_stream_should_be_deleted()
	{
		var lastNumber = ReadIndex.GetStreamLastEventNumber(_deletedEventStreamId);
		Assert.AreEqual(EventNumber.DeletedStream, lastNumber);
	}
}
