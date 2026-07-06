using System;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Processing.Checkpointing;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.Jint;

[TestFixture]
public class when_round_tripping_js_projection_with_string_state : TestFixtureWithInterpretedProjection
{
	protected override void Given()
	{
		_projection = @"
                fromAll().when({
                    type1: function(state, event) {
                        return 'hello';
                    }
                });
            ";
	}

	[Test, Category(_projectionType)]
	public void process_event_returns_json_encoded_string_state()
	{
		_stateHandler.ProcessEvent(
			"", CheckpointTag.FromPosition(0, 20, 10), "stream1", "type1", "category", Guid.NewGuid(), 0,
			"metadata", @"{""a"":""b""}", out var state, out _);

		Assert.AreEqual(@"""hello""", state);
		Assert.DoesNotThrow(() => _stateHandler.Load(state));
	}
}

[TestFixture]
public class when_round_tripping_bi_state_js_projection_with_string_state : TestFixtureWithInterpretedProjection
{
	protected override void Given()
	{
		_projection = @"
                options({
                    biState: true,
                });
                fromAll().foreachStream().when({
                    type1: function(state, event) {
                        state[0] = 'hello';
                        return state;
                    }
                });
            ";
	}

	[Test, Category(_projectionType)]
	public void process_event_returns_json_encoded_string_state()
	{
		_stateHandler.ProcessEvent(
			"", CheckpointTag.FromPosition(0, 20, 10), "stream1", "type1", "category", Guid.NewGuid(), 0,
			"metadata", @"{""a"":""b""}", out var state, out var sharedState, out _);

		Assert.AreEqual(@"""hello""", state);
		Assert.IsNull(sharedState);
		Assert.DoesNotThrow(() => _stateHandler.Load(state));
	}
}
