using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Messaging;

namespace EventStore.Projections.Core.Services.Processing;

public class RequestResponseQueueForwarder : IHandle<ClientMessage.ReadEvent>,
	IHandle<ClientMessage.ReadStreamEventsBackward>,
	IHandle<ClientMessage.ReadStreamEventsForward>,
	IHandle<ClientMessage.ReadAllEventsForward>,
	IHandle<ClientMessage.WriteEvents>,
	IHandle<ClientMessage.DeleteStream>,
	IHandle<SystemMessage.SubSystemInitialized>,
	IHandle<ProjectionCoreServiceMessage.SubComponentStarted>,
	IHandle<ProjectionCoreServiceMessage.SubComponentStopped>
{
	private readonly IPublisher _externalRequestQueue;
	private readonly IPublisher _inputQueue;

	public RequestResponseQueueForwarder(IPublisher inputQueue, IPublisher externalRequestQueue)
	{
		_inputQueue = inputQueue;
		_externalRequestQueue = externalRequestQueue;
	}

	public void Handle(ClientMessage.ReadEvent msg)
	{
		_externalRequestQueue.Publish(
			new ClientMessage.ReadEvent(
				msg.InternalCorrId, msg.CorrelationId, new PublishToWrapEnvelop(_inputQueue, msg.Envelope),
				msg.EventStreamId, msg.EventNumber, msg.ResolveLinkTos, msg.RequireLeader, msg.User));
	}

	public void Handle(ClientMessage.WriteEvents msg)
	{
		_externalRequestQueue.Publish(
			new ClientMessage.WriteEvents(
				msg.InternalCorrId, msg.CorrelationId, new PublishToWrapEnvelop(_inputQueue, msg.Envelope), true,
				msg.EventStreamId, msg.ExpectedVersion, msg.Events, msg.User));
	}

	public void Handle(ClientMessage.DeleteStream msg)
	{
		_externalRequestQueue.Publish(
			new ClientMessage.DeleteStream(
				msg.InternalCorrId, msg.CorrelationId, new PublishToWrapEnvelop(_inputQueue, msg.Envelope), true,
				msg.EventStreamId, msg.ExpectedVersion, msg.HardDelete, msg.User));
	}

	public void Handle(ClientMessage.ReadStreamEventsBackward msg)
	{
		_externalRequestQueue.Publish(
			new ClientMessage.ReadStreamEventsBackward(
				msg.InternalCorrId, msg.CorrelationId, new PublishToWrapEnvelop(_inputQueue, msg.Envelope),
				msg.EventStreamId, msg.FromEventNumber, msg.MaxCount, msg.ResolveLinkTos, msg.RequireLeader,
				msg.ValidationStreamVersion, msg.User));
	}

	public void Handle(ClientMessage.ReadStreamEventsForward msg)
	{
		_externalRequestQueue.Publish(
			new ClientMessage.ReadStreamEventsForward(
				msg.InternalCorrId, msg.CorrelationId, new PublishToWrapEnvelop(_inputQueue, msg.Envelope),
				msg.EventStreamId, msg.FromEventNumber, msg.MaxCount, msg.ResolveLinkTos, msg.RequireLeader,
				msg.ValidationStreamVersion, msg.User, replyOnExpired: false));
	}

	public void Handle(ClientMessage.ReadAllEventsForward msg)
	{
		_externalRequestQueue.Publish(
			new ClientMessage.ReadAllEventsForward(
				msg.InternalCorrId, msg.CorrelationId, new PublishToWrapEnvelop(_inputQueue, msg.Envelope),
				msg.CommitPosition, msg.PreparePosition, msg.MaxCount, msg.ResolveLinkTos, msg.RequireLeader,
				msg.ValidationTfLastCommitPosition, msg.User, replyOnExpired: false));
	}

	public void Handle(SystemMessage.SubSystemInitialized msg)
	{
		_externalRequestQueue.Publish(
			new SystemMessage.SubSystemInitialized(msg.SubSystemName));
	}

	void IHandle<ProjectionCoreServiceMessage.SubComponentStarted>.Handle(
		ProjectionCoreServiceMessage.SubComponentStarted message)
	{
		_externalRequestQueue.Publish(
			new ProjectionCoreServiceMessage.SubComponentStarted(message.SubComponent, message.InstanceCorrelationId)
		);
	}

	void IHandle<ProjectionCoreServiceMessage.SubComponentStopped>.Handle(
		ProjectionCoreServiceMessage.SubComponentStopped message)
	{
		_externalRequestQueue.Publish(
			new ProjectionCoreServiceMessage.SubComponentStopped(message.SubComponent, message.QueueId)
		);
	}
}
