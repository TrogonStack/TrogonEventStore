using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.Scavenge;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class WhenDeletingSingleStreamSpanningThrough2ChunksInDbWith3Chunks<TLogFormat, TStreamId> : ReadIndexTestScenario<TLogFormat, TStreamId>
{
	private EventRecord _event8;
	private EventRecord _delete;

	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		await WriteSingleEvent("ES", 0, new string('.', 3000), token: token);
		await WriteSingleEvent("ES", 1, new string('.', 3000), token: token);
		await WriteSingleEvent("ES", 2, new string('.', 3000), token: token);

		await WriteSingleEvent("ES", 3, new string('.', 3000), retryOnFail: true, token: token); // chunk 2
		await WriteSingleEvent("ES", 4, new string('.', 3000), token: token);

		_event8 = await WriteSingleEvent("ES2", 0, new string('.', 5000), retryOnFail: true, token: token); //chunk 3

		_delete = await WriteDelete("ES", token: token);
		Scavenge(completeLast: false, mergeChunks: false);
	}

	[Test]
	public void
		read_all_forward_does_not_return_scavenged_deleted_stream_events_and_return_remaining_plus_delete_record()
	{
		var events = ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 100).EventRecords()
			.Select(r => r.Event)
			.ToArray();
		Assert.AreEqual(2, events.Length);
		Assert.AreEqual(_event8, events[0]);
		Assert.AreEqual(_delete, events[1]);
	}

	[Test]
	public async Task
		read_all_backward_does_not_return_scavenged_deleted_stream_events_and_return_remaining_plus_delete_record()
	{
		var events = (await ReadIndex.ReadAllEventsBackward(GetBackwardReadPos(), 100, CancellationToken.None))
			.EventRecords()
			.Select(r => r.Event)
			.ToArray();
		Assert.AreEqual(2, events.Length);
		Assert.AreEqual(_event8, events[1]);
		Assert.AreEqual(_delete, events[0]);
	}

	[Test]
	public async Task read_all_backward_from_beginning_of_second_chunk_returns_no_records()
	{
		var pos = new TFPos(10000, 10000);
		var events = (await ReadIndex.ReadAllEventsBackward(pos, 100, CancellationToken.None)).EventRecords()
			.Select(r => r.Event)
			.ToArray();
		Assert.AreEqual(0, events.Length);
	}

	[Test]
	public void read_all_forward_from_beginning_of_2nd_chunk_with_max_1_record_returns_1st_record_from_3rd_chunk()
	{
		var events = ReadIndex.ReadAllEventsForward(new TFPos(10000, 10000), 100).EventRecords()
			.Take(1)
			.Select(r => r.Event)
			.ToArray();
		Assert.AreEqual(1, events.Length);
		Assert.AreEqual(_event8, events[0]);
	}

	[Test]
	public void read_all_forward_with_max_5_records_returns_2_records_from_2nd_chunk_plus_delete_record()
	{
		var events = ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 5).EventRecords()
			.Select(r => r.Event)
			.ToArray();
		Assert.AreEqual(2, events.Length);
		Assert.AreEqual(_event8, events[0]);
		Assert.AreEqual(_delete, events[1]);
	}

	[Test]
	public void is_stream_deleted_returns_true()
	{
		Assert.That(ReadIndex.IsStreamDeleted("ES"));
	}

	[Test]
	public void last_event_number_returns_stream_deleted()
	{
		Assert.AreEqual(EventNumber.DeletedStream, ReadIndex.GetStreamLastEventNumber("ES"));
	}
}
