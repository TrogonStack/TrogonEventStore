using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Services;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;
using ReadStreamResult = EventStore.Core.Services.Storage.ReaderIndex.ReadStreamResult;

namespace EventStore.Core.Tests.Services.Storage.DeletingStream;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class
	WhenWritingFewPreparesWithSameEventNumberAndCommitingDeleteOnThisVersionReadIndexShould<
		TLogFormat, TStreamId> :
	ReadIndexTestScenario<TLogFormat, TStreamId>
{
	private EventRecord _deleteTombstone;

	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		string stream = "ES";
		var eventTypeId = LogFormatHelper<TLogFormat, TStreamId>.EventTypeId;
		var (streamId, _) = await GetOrReserve(stream, token);
		var (streamDeletedEventTypeId, pos) = await GetOrReserveEventType(SystemEventTypes.StreamDeleted, token);

		var prepare1 = LogRecord.SingleWrite(_recordFactory, pos, // prepare1
			Guid.NewGuid(),
			Guid.NewGuid(),
			streamId,
			-1,
			eventTypeId,
			LogRecord.NoData,
			null,
			DateTime.UtcNow);

		(var written, pos) = await Writer.Write(prepare1, token);
		Assert.IsTrue(written);

		var prepare2 = LogRecord.SingleWrite(_recordFactory, pos, // prepare2
			Guid.NewGuid(),
			Guid.NewGuid(),
			streamId,
			-1,
			eventTypeId,
			LogRecord.NoData,
			null,
			DateTime.UtcNow);

		(written, pos) = await Writer.Write(prepare2, token);
		Assert.IsTrue(written);


		var deletePrepare = LogRecord.DeleteTombstone(_recordFactory, pos, // delete prepare
			Guid.NewGuid(), Guid.NewGuid(), streamId, streamDeletedEventTypeId, -1);
		_deleteTombstone = new EventRecord(EventNumber.DeletedStream, deletePrepare, stream,
			SystemEventTypes.StreamDeleted);

		(written, pos) = await Writer.Write(deletePrepare, token);
		Assert.IsTrue(written);

		var prepare3 = LogRecord.SingleWrite(_recordFactory, pos, // prepare3
			Guid.NewGuid(),
			Guid.NewGuid(),
			streamId,
			-1,
			eventTypeId,
			LogRecord.NoData,
			null,
			DateTime.UtcNow);

		(written, pos) = await Writer.Write(prepare3, token);
		Assert.IsTrue(written);

		var commit = LogRecord.Commit(pos, // committing delete
			deletePrepare.CorrelationId,
			deletePrepare.LogPosition,
			EventNumber.DeletedStream);
		Assert.IsTrue(await Writer.Write(commit, token) is (true, _));
	}

	[Test]
	public void indicate_that_stream_is_deleted()
	{
		Assert.That(ReadIndex.IsStreamDeleted("ES"));
	}

	[Test]
	public void indicate_that_nonexisting_stream_with_same_hash_is_not_deleted()
	{
		Assert.That(ReadIndex.IsStreamDeleted("ZZ"), Is.False);
	}

	[Test]
	public void indicate_that_nonexisting_stream_with_different_hash_is_not_deleted()
	{
		Assert.That(ReadIndex.IsStreamDeleted("XXX"), Is.False);
	}

	[Test]
	public void read_single_events_with_number_0_should_return_stream_deleted()
	{
		var result = ReadIndex.ReadEvent("ES", 0);
		Assert.AreEqual(ReadEventResult.StreamDeleted, result.Result);
		Assert.IsNull(result.Record);
	}

	[Test]
	public void read_single_events_with_number_1_should_return_stream_deleted()
	{
		var result = ReadIndex.ReadEvent("ES", 1);
		Assert.AreEqual(ReadEventResult.StreamDeleted, result.Result);
		Assert.IsNull(result.Record);
	}

	[Test]
	public void read_stream_events_forward_should_return_stream_deleted()
	{
		var result = ReadIndex.ReadStreamEventsForward("ES", 0, 100);
		Assert.AreEqual(ReadStreamResult.StreamDeleted, result.Result);
		Assert.AreEqual(0, result.Records.Length);
	}

	[Test]
	public void read_stream_events_backward_should_return_stream_deleted()
	{
		var result = ReadIndex.ReadStreamEventsBackward("ES", -1, 100);
		Assert.AreEqual(ReadStreamResult.StreamDeleted, result.Result);
		Assert.AreEqual(0, result.Records.Length);
	}

	[Test]
	public void read_all_forward_should_return_all_stream_records_except_uncommited()
	{
		var events = ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 100).EventRecords()
			.Select(r => r.Event)
			.ToArray();
		Assert.AreEqual(1, events.Length);
		Assert.AreEqual(_deleteTombstone, events[0]);
	}

	[Test]
	public async Task read_all_backward_should_return_all_stream_records_except_uncommited()
	{
		var events = (await ReadIndex.ReadAllEventsBackward(GetBackwardReadPos(), 100, CancellationToken.None))
			.EventRecords()
			.Select(r => r.Event)
			.ToArray();
		Assert.AreEqual(1, events.Length);
		Assert.AreEqual(_deleteTombstone, events[0]);
	}
}
