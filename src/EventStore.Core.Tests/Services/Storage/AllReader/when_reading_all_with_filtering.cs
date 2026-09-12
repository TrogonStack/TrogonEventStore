using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Services.Storage.ReaderIndex;
using NUnit.Framework;


namespace EventStore.Core.Tests.Services.Storage.AllReader;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class WhenReadingAllWithFiltering<TLogFormat, TStreamId> : ReadIndexTestScenario<TLogFormat, TStreamId>
{
	TFPos _forwardReadPos;
	TFPos _backwardReadPos;

	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		var firstEvent = await WriteSingleEvent("ES1", 1, new string('.', 3000), eventId: Guid.NewGuid(),
			eventType: "event-type-1", retryOnFail: true, token: token);
		await WriteSingleEvent("ES2", 1, new string('.', 3000), eventId: Guid.NewGuid(),
			eventType: "other-event-type-2",
			retryOnFail: true, token: token);
		await WriteSingleEvent("ES3", 1, new string('.', 3000), eventId: Guid.NewGuid(), eventType: "event-type-3",
			retryOnFail: true, token: token);
		await WriteSingleEvent("ES4", 1, new string('.', 3000), eventId: Guid.NewGuid(),
			eventType: "other-event-type-4",
			retryOnFail: true, token: token);

		_forwardReadPos = new TFPos(firstEvent.LogPosition, firstEvent.LogPosition);
		_backwardReadPos = new TFPos(Writer.Position, Writer.Position);
	}

	[Test]
	public async Task should_read_only_events_forward_with_event_type_prefix()
	{
		var eventFilter = EventFilter.EventType.Prefixes(true, ["event-type"]);

		var result = await ReadIndex.ReadAllEventsForwardFiltered(_forwardReadPos, 10, 10, eventFilter, CancellationToken.None);
		Assert.AreEqual(2, result.Records.Count);
	}

	[Test]
	public async Task should_read_only_events_forward_with_event_type_regex()
	{
		var eventFilter = EventFilter.EventType.Regex(true, @"^.*other-event.*$");

		var result = await ReadIndex.ReadAllEventsForwardFiltered(_forwardReadPos, 10, 10, eventFilter, CancellationToken.None);
		Assert.AreEqual(2, result.Records.Count);
	}

	[Test]
	public async Task should_read_only_events_forward_with_stream_id_prefix()
	{
		var eventFilter = EventFilter.StreamName.Prefixes(true, ["ES2"]);

		var result = await ReadIndex.ReadAllEventsForwardFiltered(_forwardReadPos, 10, 10, eventFilter, CancellationToken.None);
		Assert.AreEqual(1, result.Records.Count);
	}

	[Test]
	public async Task should_read_only_events_forward_with_stream_id_regex()
	{
		var eventFilter = EventFilter.StreamName.Regex(true, @"^.*ES2.*$");

		var result = await ReadIndex.ReadAllEventsForwardFiltered(_forwardReadPos, 10, 10, eventFilter, CancellationToken.None);
		Assert.AreEqual(1, result.Records.Count);
	}

	[Test]
	public async Task should_read_only_events_backward_with_event_type_prefix()
	{
		var eventFilter = EventFilter.EventType.Prefixes(true, ["event-type"]);

		var result =
			await ReadIndex.ReadAllEventsBackwardFiltered(_backwardReadPos, 10, 10, eventFilter,
				CancellationToken.None);
		Assert.AreEqual(2, result.Records.Count);
	}

	[Test]
	public async Task should_read_only_events_backward_with_event_type_regex()
	{
		var eventFilter = EventFilter.EventType.Regex(true, @"^.*other-event.*$");

		var result =
			await ReadIndex.ReadAllEventsBackwardFiltered(_backwardReadPos, 10, 10, eventFilter,
				CancellationToken.None);
		Assert.AreEqual(2, result.Records.Count);
	}

	[Test]
	public async Task should_read_only_events_backward_with_stream_id_prefix()
	{
		var eventFilter = EventFilter.StreamName.Prefixes(true, ["ES2"]);

		var result =
			await ReadIndex.ReadAllEventsBackwardFiltered(_backwardReadPos, 10, 10, eventFilter,
				CancellationToken.None);
		Assert.AreEqual(1, result.Records.Count);
	}

	[Test]
	public async Task should_read_only_events_backward_with_stream_id_regex()
	{
		var eventFilter = EventFilter.StreamName.Regex(true, @"^.*ES2.*$");

		var result =
			await ReadIndex.ReadAllEventsBackwardFiltered(_backwardReadPos, 10, 10, eventFilter,
				CancellationToken.None);
		Assert.AreEqual(1, result.Records.Count);
	}
}
