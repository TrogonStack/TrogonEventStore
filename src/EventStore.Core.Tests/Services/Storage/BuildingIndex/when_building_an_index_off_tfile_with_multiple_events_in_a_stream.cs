using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;
using ReadStreamResult = EventStore.Core.Services.Storage.ReaderIndex.ReadStreamResult;

namespace EventStore.Core.Tests.Services.Storage.BuildingIndex;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class
	WhenBuildingAnIndexOffTfileWithMultipleEventsInAStream<TLogFormat, TStreamId> : ReadIndexTestScenario<
	TLogFormat, TStreamId>
{
	private Guid _id1;
	private Guid _id2;

	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		_id1 = Guid.NewGuid();
		_id2 = Guid.NewGuid();
		var eventTypeId = LogFormatHelper<TLogFormat, TStreamId>.EventTypeId;

		var (streamId, pos0) = await GetOrReserve("test1", token);

		var (_, pos1) = await Writer.Write(LogRecord.SingleWrite(_recordFactory, pos0, _id1, _id1, streamId,
		ExpectedVersion.NoStream,
		eventTypeId, new byte[0], new byte[0], DateTime.UtcNow), token);
		var (_, pos2) = await Writer.Write(LogRecord.SingleWrite(_recordFactory, pos1, _id2, _id2, streamId, 0,
			eventTypeId, new byte[0], new byte[0]), token);
		var (_, pos3) = await Writer.Write(new CommitLogRecord(pos2, _id1, pos0, DateTime.UtcNow, 0), token);
		await Writer.Write(new CommitLogRecord(pos3, _id2, pos1, DateTime.UtcNow, 1), token);
	}

	[Test]
	public void no_event_is_returned_when_nonexistent_stream_is_requested()
	{
		var result = ReadIndex.ReadEvent("test2", 0);
		Assert.AreEqual(ReadEventResult.NoStream, result.Result);
		Assert.IsNull(result.Record);
	}

	[Test]
	public void the_first_event_can_be_read()
	{
		var result = ReadIndex.ReadEvent("test1", 0);
		Assert.AreEqual(ReadEventResult.Success, result.Result);
		Assert.AreEqual(_id1, result.Record.EventId);
	}

	[Test]
	public void the_second_event_can_be_read()
	{
		var result = ReadIndex.ReadEvent("test1", 1);
		Assert.AreEqual(ReadEventResult.Success, result.Result);
		Assert.AreEqual(_id2, result.Record.EventId);
	}

	[Test]
	public void the_third_event_is_not_found()
	{
		var result = ReadIndex.ReadEvent("test1", 2);
		Assert.AreEqual(ReadEventResult.NotFound, result.Result);
		Assert.IsNull(result.Record);
	}

	[Test]
	public void the_last_event_is_returned()
	{
		var result = ReadIndex.ReadEvent("test1", -1);
		Assert.AreEqual(ReadEventResult.Success, result.Result);
		Assert.AreEqual(_id2, result.Record.EventId);
	}

	[Test]
	public void the_stream_can_be_read_with_two_events_in_right_order_when_starting_from_specified_event_number()
	{
		var result = ReadIndex.ReadStreamEventsBackward("test1", 1, 10);
		Assert.AreEqual(ReadStreamResult.Success, result.Result);
		Assert.AreEqual(2, result.Records.Length);

		Assert.AreEqual(_id1, result.Records[1].EventId);
		Assert.AreEqual(_id2, result.Records[0].EventId);
	}

	[Test]
	public void the_stream_can_be_read_with_two_events_backward_from_end()
	{
		var result = ReadIndex.ReadStreamEventsBackward("test1", -1, 10);
		Assert.AreEqual(ReadStreamResult.Success, result.Result);
		Assert.AreEqual(2, result.Records.Length);

		Assert.AreEqual(_id1, result.Records[1].EventId);
		Assert.AreEqual(_id2, result.Records[0].EventId);
	}

	[Test]
	public void the_stream_returns_events_with_correct_pagination()
	{
		var result = ReadIndex.ReadStreamEventsBackward("test1", 0, 10);
		Assert.AreEqual(ReadStreamResult.Success, result.Result);
		Assert.AreEqual(1, result.Records.Length);

		Assert.AreEqual(_id1, result.Records[0].EventId);
	}

	[Test]
	public void the_stream_returns_nothing_for_nonexistent_page()
	{
		var result = ReadIndex.ReadStreamEventsBackward("test1", 100, 10);
		Assert.AreEqual(ReadStreamResult.Success, result.Result);
		Assert.AreEqual(0, result.Records.Length);
	}

	[Test]
	public void no_events_are_return_if_event_stream_doesnt_exist()
	{
		var result = ReadIndex.ReadStreamEventsBackward("test2", 0, 10);
		Assert.AreEqual(ReadStreamResult.NoStream, result.Result);
		Assert.IsNotNull(result.Records);
		Assert.IsEmpty(result.Records);
	}

	[Test]
	public void read_all_events_forward_returns_all_events_in_correct_order()
	{
		var records = ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 10).EventRecords();

		Assert.AreEqual(2, records.Count);
		Assert.AreEqual(_id1, records[0].Event.EventId);
		Assert.AreEqual(_id2, records[1].Event.EventId);
	}

	[Test]
	public async Task read_all_events_backward_returns_all_events_in_correct_order()
	{
		var records = (await ReadIndex.ReadAllEventsBackward(GetBackwardReadPos(), 10, CancellationToken.None))
			.EventRecords();

		Assert.AreEqual(2, records.Count);
		Assert.AreEqual(_id1, records[1].Event.EventId);
		Assert.AreEqual(_id2, records[0].Event.EventId);
	}
}
