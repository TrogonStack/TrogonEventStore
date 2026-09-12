using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Tests;
using EventStore.Projections.Core.Services.Processing;
using EventStore.Projections.Core.Services.Processing.Checkpointing;
using EventStore.Projections.Core.Services.Processing.Emitting;
using EventStore.Projections.Core.Services.Processing.Emitting.EmittedEvents;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.emitted_stream_manager.when_tracking;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class with_tracking_enabled_with_duplicate_event_streams<TLogFormat, TStreamId> : SpecificationWithEmittedStreamsTrackerAndDeleter<TLogFormat, TStreamId>
{
	private CountdownEvent _eventAppeared = new CountdownEvent(2);

	protected override TimeSpan Timeout { get; } = TimeSpan.FromSeconds(10);

	protected override async Task When()
	{
		_emittedStreamsTracker.TrackEmittedStream(new EmittedEvent[] {
			new EmittedDataEvent(
				"test_stream", Guid.NewGuid(), "type1", true,
				"data", null, CheckpointTag.FromPosition(0, 100, 50), null, null),
			new EmittedDataEvent(
				"test_stream", Guid.NewGuid(), "type1", true,
				"data", null, CheckpointTag.FromPosition(0, 100, 50), null, null)
		});

		var events = await WaitForEvents(_projectionNamesBuilder.GetEmittedStreamsName(), 1);
		if (events.Length == 1)
			_eventAppeared.Signal();
	}

	[Test]
	public async Task should_at_best_attempt_to_track_a_unique_list_of_streams()
	{
		var events = await ReadEvents(_projectionNamesBuilder.GetEmittedStreamsName(), 200);
		Assert.AreEqual(1, events.Length);
		Assert.AreEqual("test_stream", Helper.UTF8NoBom.GetString(events[0].Event.Data.ToByteArray()));
		Assert.AreEqual(1, _eventAppeared.CurrentCount); //only 1 event appeared should get through
	}
}
