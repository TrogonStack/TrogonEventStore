using EventStore.Projections.Core.Services.Processing.Checkpointing;

namespace EventStore.Projections.Core.Services.Processing.Emitting.EmittedEvents;

sealed class ValidEmittedEvent : IValidatedEmittedEvent
{
	public CheckpointTag Checkpoint { get; private set; }
	public string EventType { get; private set; }
	public long Revision { get; private set; }

	public ValidEmittedEvent(CheckpointTag checkpoint, string eventType, long revision)
	{
		Checkpoint = checkpoint;
		EventType = eventType;
		Revision = revision;
	}
}
