using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.BuildingIndex;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class
	WhenBuildingAnIndexOffTfileWithPreparesButNoCommits<TLogFormat, TStreamId> : ReadIndexTestScenario<
	TLogFormat,
	TStreamId>
{
	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		var (streamId1, _) = await GetOrReserve("test1", token);
		var (streamId2, _) = await GetOrReserve("test2", token);
		var (streamId3, p0) = await GetOrReserve("test3", token);

		var eventTypeId = LogFormatHelper<TLogFormat, TStreamId>.EventTypeId;

		var (_, p1) = await Writer.Write(LogRecord.Prepare(_logFormat.RecordFactory, p0, Guid.NewGuid(), Guid.NewGuid(),
				p0, 0, streamId1, -1,
				PrepareFlags.SingleWrite, eventTypeId, Array.Empty<byte>(), Array.Empty<byte>(), DateTime.UtcNow),
			token);
		var (_, p2) = await Writer.Write(LogRecord.Prepare(_logFormat.RecordFactory, p1, Guid.NewGuid(), Guid.NewGuid(),
				p1, 0, streamId2, -1,
				PrepareFlags.SingleWrite, eventTypeId, Array.Empty<byte>(), Array.Empty<byte>(), DateTime.UtcNow),
			token);

		await Writer.Write(LogRecord.Prepare(_logFormat.RecordFactory, p2, Guid.NewGuid(), Guid.NewGuid(), p2, 0,
				streamId3, -1,
				PrepareFlags.SingleWrite, eventTypeId, Array.Empty<byte>(), Array.Empty<byte>(), DateTime.UtcNow),
			token);
	}

	[Test]
	public void the_first_stream_is_not_in_index_yet()
	{
		var result = ReadIndex.ReadEvent("test1", 0);
		Assert.AreEqual(ReadEventResult.NoStream, result.Result);
		Assert.IsNull(result.Record);
	}

	[Test]
	public void the_second_stream_is_not_in_index_yet()
	{
		var result = ReadIndex.ReadEvent("test2", 0);
		Assert.AreEqual(ReadEventResult.NoStream, result.Result);
		Assert.IsNull(result.Record);
	}

	[Test]
	public void the_last_event_is_not_returned_for_stream()
	{
		var result = ReadIndex.ReadEvent("test2", -1);
		Assert.AreEqual(ReadEventResult.NoStream, result.Result);
		Assert.IsNull(result.Record);
	}

	[Test]
	public void read_all_events_forward_returns_no_events()
	{
		var records = ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 10).EventRecords();
		Assert.AreEqual(0, records.Count);
	}

	[Test]
	public async Task read_all_events_backward_returns_no_events()
	{
		var records = (await ReadIndex.ReadAllEventsBackward(GetBackwardReadPos(), 10, CancellationToken.None))
			.EventRecords();
		Assert.AreEqual(0, records.Count);
	}
}
