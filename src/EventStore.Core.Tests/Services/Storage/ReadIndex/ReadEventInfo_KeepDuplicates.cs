using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Tests.Index.Hashers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.ReadIndex;

[TestFixture]
public class ReadEventInfo_KeepDuplicates() : ReadIndexTestScenario<LogFormat.V2, string>(maxEntriesInMemTable: 3,
	lowHasher: new ConstantHasher(0),
	highHasher: new HumanReadableHasher32())
{
	private const string Stream = "ab-1";
	private const string CollidingStream = "cb-1";
	private const string SoftDeletedStream = "de-1";
	private const string HardDeletedStream = "fg-1";

	private readonly List<EventRecord> _events = [];

	private static void CheckResult(EventRecord[] events, IndexReadEventInfoResult result)
	{
		Assert.AreEqual(events.Length, result.EventInfos.Length);
		for (int i = 0; i < events.Length; i++)
		{
			Assert.AreEqual(events[i].EventNumber, result.EventInfos[i].EventNumber);
			Assert.AreEqual(events[i].LogPosition, result.EventInfos[i].LogPosition);
		}
	}

	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		// PTable 1
		_events.Add(await WriteSingleEvent(Stream, 0, string.Empty, token: token));
		_events.Add(await WriteSingleEvent(Stream, 1, string.Empty, token: token));
		_events.Add(await WriteSingleEvent(Stream, 2, string.Empty, token: token));

		// PTable 2
		_events.Add(await WriteSingleEvent(Stream, 3, string.Empty, token: token));
		_events.Add(await WriteSingleEvent(Stream, 2, string.Empty, token: token)); // duplicate
		_events.Add(await WriteSingleEvent(CollidingStream, 3, string.Empty, token: token)); // colliding stream

		// PTable 3
		_events.Add(await WriteSingleEvent(Stream, 2, string.Empty, token: token)); // duplicate
		_events.Add(await WriteSingleEvent(SoftDeletedStream, 10, string.Empty, token: token)); // soft deleted stream
		_events.Add(await WriteSingleEvent(HardDeletedStream, 20, string.Empty, token: token)); // hard deleted stream

		// MemTable
		await WriteStreamMetadata(SoftDeletedStream, 0, @"{""$tb"":11}", token: token);
		await WriteDelete(HardDeletedStream, token);
	}

	[Test]
	public void returns_correct_info_for_normal_event()
	{
		var result = ReadIndex.ReadEventInfo_KeepDuplicates(Stream, 1);
		var events = _events
			.Where(x => x.EventStreamId == Stream && x.EventNumber == 1)
			.ToArray();

		Assert.AreEqual(1, events.Length);
		Assert.AreEqual(-1, result.NextEventNumber);
		Assert.AreEqual(true, result.IsEndOfStream);
		CheckResult(events, result);
	}

	[Test]
	public void returns_correct_info_for_duplicate_events()
	{
		var result = ReadIndex.ReadEventInfo_KeepDuplicates(Stream, 2);
		var events = _events
			.Where(x => x.EventStreamId == Stream && x.EventNumber == 2)
			.ToArray();

		Assert.AreEqual(3, events.Length);
		Assert.AreEqual(-1, result.NextEventNumber);
		Assert.AreEqual(true, result.IsEndOfStream);
		CheckResult(events, result);
	}

	[Test]
	public void returns_correct_info_for_colliding_stream()
	{
		var result = ReadIndex.ReadEventInfo_KeepDuplicates(Stream, 3);
		var events = _events
			.Where(x => x.EventStreamId == Stream && x.EventNumber == 3)
			.ToArray();

		Assert.AreEqual(1, events.Length);
		Assert.AreEqual(-1, result.NextEventNumber);
		Assert.AreEqual(true, result.IsEndOfStream);
		CheckResult(events, result);

		result = ReadIndex.ReadEventInfo_KeepDuplicates(CollidingStream, 3);
		events = _events
			.Where(x => x.EventStreamId == CollidingStream && x.EventNumber == 3)
			.ToArray();

		Assert.AreEqual(1, events.Length);
		Assert.AreEqual(-1, result.NextEventNumber);
		Assert.AreEqual(true, result.IsEndOfStream);
		CheckResult(events, result);
	}

	[Test]
	public void returns_correct_info_for_soft_deleted_stream()
	{
		var result = ReadIndex.ReadEventInfo_KeepDuplicates(SoftDeletedStream, 10);
		var events = _events
			.Where(x => x.EventStreamId == SoftDeletedStream && x.EventNumber == 10)
			.ToArray();

		Assert.AreEqual(1, events.Length);
		Assert.AreEqual(-1, result.NextEventNumber);
		Assert.AreEqual(true, result.IsEndOfStream);
		CheckResult(events, result);
	}

	[Test]
	public void returns_correct_info_for_hard_deleted_stream()
	{
		var result = ReadIndex.ReadEventInfo_KeepDuplicates(HardDeletedStream, 20);
		var events = _events
			.Where(x => x.EventStreamId == HardDeletedStream && x.EventNumber == 20)
			.ToArray();

		Assert.AreEqual(1, events.Length);
		Assert.AreEqual(-1, result.NextEventNumber);
		Assert.AreEqual(true, result.IsEndOfStream);
		CheckResult(events, result);
	}

	[Test]
	public void returns_empty_info_when_event_does_not_exist()
	{
		var result = ReadIndex.ReadEventInfo_KeepDuplicates(Stream, 6);
		var events = _events
			.Where(x => x.EventStreamId == Stream && x.EventNumber == 6)
			.ToArray();

		Assert.AreEqual(0, events.Length);
		Assert.AreEqual(-1, result.NextEventNumber);
		Assert.AreEqual(true, result.IsEndOfStream);

		result = ReadIndex.ReadEventInfo_KeepDuplicates(CollidingStream, 4);
		events = _events
			.Where(x => x.EventStreamId == CollidingStream && x.EventNumber == 4)
			.ToArray();

		Assert.AreEqual(0, events.Length);
		Assert.AreEqual(-1, result.NextEventNumber);
		Assert.AreEqual(true, result.IsEndOfStream);
	}
}
