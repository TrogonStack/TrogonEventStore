using System;
using System.Collections.Generic;
using System.Diagnostics;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Processing;
using EventStore.Projections.Core.Services.Processing.Checkpointing;
using EventStore.Projections.Core.Services.Processing.Emitting;
using EventStore.Projections.Core.Services.Processing.Emitting.EmittedEvents;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.Jint;

[TestFixture]
public class when_running_emitting_js_projection : TestFixtureWithInterpretedProjection
{
	protected override void Given()
	{
		_projection = @"
                fromAll().when({$any: 
                    function(state, event) {
					let targetMetadata = Object.assign({}, event.metadata);
					targetMetadata.g = 'h';
					targetMetadata.i = 10;
                    emit('output-stream' + event.sequenceNumber, 'emitted-event' + event.sequenceNumber, {a: JSON.parse(event.bodyRaw).a}, targetMetadata);
                    return {};
                }});
            ";
	}

	[Test, Category(_projectionType)]
	public void process_event_returns_true()
	{
		string state;
		EmittedEventEnvelope[] emittedEvents;
		var result = _stateHandler.ProcessEvent(
			"", CheckpointTag.FromPosition(0, 20, 10), "stream1", "type1", "category", Guid.NewGuid(), 0,
			"{\"d\":\"e\"}",
			@"{""a"":""b""}", out state, out emittedEvents);

		Assert.IsTrue(result);
	}

	[Test, Category(_projectionType)]
	public void process_event_returns_emitted_event()
	{
		string state;
		EmittedEventEnvelope[] emittedEvents;
		_stateHandler.ProcessEvent(
			"", CheckpointTag.FromPosition(0, 20, 10), "stream1", "type1", "category", Guid.NewGuid(), 0,
			"{\"d\":\"e\"}",
			@"{""a"":""b""}", out state, out emittedEvents);

		Assert.IsNotNull(emittedEvents);
		Assert.AreEqual(1, emittedEvents.Length);
		Assert.AreEqual("emitted-event0", emittedEvents[0].Event.EventType);
		Assert.AreEqual("output-stream0", emittedEvents[0].Event.StreamId);
		Assert.AreEqual(@"{""a"":""b""}", emittedEvents[0].Event.Data);
		var metadata = new Dictionary<string, string>(emittedEvents[0].Event.ExtraMetaData());
		Assert.AreEqual("\"e\"", metadata["d"]);
		Assert.AreEqual("\"h\"", metadata["g"]);
		Assert.AreEqual("10", metadata["i"]);
	}
	//todo: actual benchmark
	[Test, Category(_projectionType), Category("Manual"), Explicit]
	public void can_pass_though_millions_of_events()
	{
		var i = 0;
		var sw = Stopwatch.StartNew();

		for (i = 0; i < 100000000; i++)
		{
			string state;
			EmittedEventEnvelope[] emittedEvents;
			_stateHandler.ProcessEvent(
				"", CheckpointTag.FromPosition(0, i * 10 + 20, i * 10 + 10), "stream" + i, "type" + i, "category",
				Guid.NewGuid(), i,
				"{\"d\":\"e\"}", @"{""a"":""" + i + @"""}", out state, out emittedEvents);

			Assert.IsNotNull(emittedEvents);
			Assert.AreEqual(1, emittedEvents.Length);
			Assert.AreEqual("emitted-event" + i, emittedEvents[0].Event.EventType);
			Assert.AreEqual("output-stream" + i, emittedEvents[0].Event.StreamId);
			Assert.AreEqual(@"{""a"":""" + i + @"""}", emittedEvents[0].Event.Data);

			if (sw.Elapsed > TimeSpan.FromSeconds(120))
				break;
		}

		Console.WriteLine($"processed: {i}");
	}
}
