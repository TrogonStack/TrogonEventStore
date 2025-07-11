using System;
using EventStore.Projections.Core.Services.Processing.Checkpointing;
using EventStore.Projections.Core.Services.Processing.Emitting.EmittedEvents;

namespace EventStore.Projections.Core.Services.Processing.Emitting;

public class NoopResultEventEmitter : IResultEventEmitter
{
	public EmittedEventEnvelope[] ResultUpdated(string partition, string result, CheckpointTag at)
	{
		throw new NotSupportedException("No results are expected from the projection");
	}
}
