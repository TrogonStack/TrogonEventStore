using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.Scavenge;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class
	WhenDeletingSingleStreamSpanningThrough2ChunksIn2NdChunkInDbWith3Chunks<TLogFormat, TStreamId> : ReadIndexTestScenario<TLogFormat, TStreamId>
{
	private EventRecord _event7;
	private EventRecord _event9;

	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		await WriteSingleEvent("ES", 0, new string('.', 3000), token: token); // chunk 1
		await WriteSingleEvent("ES", 1, new string('.', 3000), token: token);
		await WriteSingleEvent("ES", 2, new string('.', 3000), token: token);

		await WriteSingleEvent("ES", 3, new string('.', 3000), retryOnFail: true, token: token); // chunk 2
		await WriteSingleEvent("ES", 4, new string('.', 3000), token: token);

		_event7 = await WriteDelete("ES", token);
		_event9 = await WriteSingleEvent("ES2", 0, new string('.', 5000), retryOnFail: true, token: token); //chunk 3

		Scavenge(completeLast: false, mergeChunks: false);
	}

	[Test]
	public void read_all_forward_does_not_return_scavenged_deleted_stream_events_and_return_remaining()
	{
		var events = ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 100).EventRecords()
			.Select(r => r.Event)
			.ToArray();
		Assert.AreEqual(2, events.Length);
		Assert.AreEqual(_event7, events[0]);
		Assert.AreEqual(_event9, events[1]);
	}

	[Test]
	public async Task read_all_backward_does_not_return_scavenged_deleted_stream_events_and_return_remaining()
	{
		var events = (await ReadIndex.ReadAllEventsBackward(GetBackwardReadPos(), 100, CancellationToken.None))
			.EventRecords()
			.Select(r => r.Event)
			.ToArray();
		Assert.AreEqual(2, events.Length);
		Assert.AreEqual(_event7, events[1]);
		Assert.AreEqual(_event9, events[0]);
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
	public void
		read_all_forward_from_beginning_of_2nd_chunk_with_max_2_record_returns_delete_record_and_record_from_3rd_chunk()
	{
		var events = ReadIndex.ReadAllEventsForward(new TFPos(10000, 10000), 100).EventRecords()
			.Take(2)
			.Select(r => r.Event)
			.ToArray();
		Assert.AreEqual(2, events.Length);
		Assert.AreEqual(_event7, events[0]);
		Assert.AreEqual(_event9, events[1]);
	}

	[Test]
	public void read_all_forward_with_max_5_records_returns_2_records_from_2nd_chunk()
	{
		var events = ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 5).EventRecords()
			.Select(r => r.Event)
			.ToArray();
		Assert.AreEqual(2, events.Length);
		Assert.AreEqual(_event7, events[0]);
		Assert.AreEqual(_event9, events[1]);
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

	[Test]
	public void last_physical_record_from_scavenged_stream_should_remain()
	{
		// cannot use readIndex here as it doesn't return deleteTombstone

		var chunk = Db.Manager.GetChunk(1);
		var chunkPos = (int)(_event7.LogPosition % Db.Config.ChunkSize);
		var res = chunk.TryReadAt(chunkPos, couldBeScavenged: false);

		Assert.IsTrue(res.Success);
	}
}
