using System;
using System.Text;
using System.Text.Json;
using EventStore.Core.Data;
using NUnit.Framework;
using ResolvedEvent = EventStore.Projections.Core.Services.Processing.ResolvedEvent;

namespace EventStore.Projections.Core.Tests.Services.Jint;

[TestFixture(DateTimeKind.Utc)]
[TestFixture(DateTimeKind.Unspecified)]
public class when_accessing_event_created_property : specification_with_event_handled
{
	private readonly DateTimeKind _timestampKind;

	public when_accessing_event_created_property(DateTimeKind timestampKind)
	{
		_timestampKind = timestampKind;
	}

	private DateTime ExpectedTimestamp => new(2023, 4, 5, 12, 34, 56, _timestampKind);

	protected override void Given()
	{
		_projection = @"
            fromAll().when({$any:
                function(state, event) {
                    return { created: event.created };
                }
            });
        ";
		_state = @"{}";
		_handledEvent = new ResolvedEvent(
			positionStreamId: "test-stream",
			positionSequenceNumber: 42,
			eventStreamId: "test-stream",
			eventSequenceNumber: 42,
			resolvedLinkTo: false,
			position: new TFPos(100, 50),
			eventOrLinkTargetPosition: new TFPos(100, 50),
			eventId: Guid.NewGuid(),
			eventType: "TestEvent",
			isJson: true,
			data: Encoding.UTF8.GetBytes("{}"),
			metadata: Encoding.UTF8.GetBytes("{}"),
			positionMetadata: Array.Empty<byte>(),
			streamMetadata: null,
			timestamp: ExpectedTimestamp);
	}

	[Test, Category(_projectionType)]
	public void exposes_the_event_timestamp_as_an_iso_8601_string()
	{
		using var state = JsonDocument.Parse(_newState);

		var expectedTimestamp = DateTime.SpecifyKind(ExpectedTimestamp, DateTimeKind.Utc).ToString("o");

		Assert.That(state.RootElement.GetProperty("created").GetString(), Is.EqualTo(expectedTimestamp));
	}
}
