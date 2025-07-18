using System;
using System.Text;
using EventStore.Common.Utils;
using EventStore.Core.Data;
using EventStore.Core.Tests;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Services;
using NUnit.Framework;
using ResolvedEvent = EventStore.Projections.Core.Services.Processing.ResolvedEvent;

namespace EventStore.Projections.Core.Tests.Services.core_projection;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class
	when_starting_an_existing_projection_missing_last_state_snapshot<TLogFormat, TStreamId> : TestFixtureWithCoreProjectionStarted<TLogFormat, TStreamId>
{
	private readonly Guid _causedByEventId = Guid.NewGuid();

	protected override void Given()
	{
		ExistingEvent(
			"$projections-projection-result", "Result", @"{""c"": 100, ""p"": 50}", "{}");
		ExistingEvent(
			"$projections-projection-checkpoint", ProjectionEventTypes.ProjectionCheckpoint,
			@"{""c"": 100, ""p"": 50}", "{}");
		ExistingEvent(
			FakeProjectionStateHandler._emit1StreamId, FakeProjectionStateHandler._emit1EventType,
			@"{""c"": 120, ""p"": 110}", FakeProjectionStateHandler._emit1Data);
		NoStream("$projections-projection-order");
		AllWritesToSucceed("$projections-projection-order");
	}

	protected override void When()
	{
		//projection subscribes here
		_bus.Publish(
			EventReaderSubscriptionMessage.CommittedEventReceived.Sample(
				new ResolvedEvent(
					"/event_category/1", -1, "/event_category/1", -1, false, new TFPos(120, 110),
					_causedByEventId, "emit1_type", false, "data",
					"metadata"), _subscriptionId, 0));
	}

	[Test]
	public void should_not_emit_events_but_write_the_new_state_snapshot()
	{
		Assert.AreEqual(2, _writeEventHandler.HandledMessages.Count);

		var data = Helper.UTF8NoBom.GetString(_writeEventHandler.HandledMessages[1].Events[0].Data);
		Assert.AreEqual("data", data);
	}
}
