using System;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Processing.Checkpointing;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.Jint;

[TestFixture]
public class when_accessing_event_body_with_non_object_data : TestFixtureWithInterpretedProjection
{
	protected override void Given()
	{
		_projection = @"
                fromAll().when({$any:
                    function(state, event) {
                        return event.body;
                    }
                });
            ";
	}

	[TestCase("null", null)]
	[TestCase("42", "42")]
	[TestCase(@"""hello""", @"""hello""")]
	[TestCase("true", "true")]
	[Category(_projectionType)]
	public void process_event_returns_json_primitive_body(string data, string expectedState)
	{
		_stateHandler.ProcessEvent(
			"", CheckpointTag.FromPosition(0, 20, 10), "stream1", "type1", "category", Guid.NewGuid(), 0,
			"metadata", data, out var state, out _);

		Assert.AreEqual(expectedState, state);
	}
}
