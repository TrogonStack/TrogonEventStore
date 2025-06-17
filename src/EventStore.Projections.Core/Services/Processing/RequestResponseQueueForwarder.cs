using System;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Messaging;

namespace EventStore.Projections.Core.Services.Processing;

public class RequestResponseQueueForwarder(IPublisher inputQueue, IPublisher externalRequestQueue)
	: IHandle<ClientMessage.ReadEvent>,
		IHandle<ClientMessage.ReadStreamEventsBackward>,
		IHandle<ClientMessage.ReadStreamEventsForward>,
		IHandle<ClientMessage.ReadAllEventsForward>,
		IHandle<ClientMessage.WriteEvents>,
		IHandle<ClientMessage.DeleteStream>,
		IHandle<SystemMessage.SubSystemInitialized>,
		IHandle<ProjectionCoreServiceMessage.SubComponentStarted>,
		IHandle<ProjectionCoreServiceMessage.SubComponentStopped>
{
	public void Handle(ClientMessage.ReadEvent msg) =>
		externalRequestQueue.Publish(
			new ClientMessage.ReadEvent(
				msg.InternalCorrId, msg.CorrelationId, new PublishToWrapEnvelop(inputQueue, msg.Envelope),
				msg.EventStreamId, msg.EventNumber, msg.ResolveLinkTos, msg.RequireLeader, msg.User));

	public void Handle(ClientMessage.WriteEvents msg) =>
		externalRequestQueue.Publish(
			new ClientMessage.WriteEvents(
				msg.InternalCorrId, msg.CorrelationId, new PublishToWrapEnvelop(inputQueue, msg.Envelope), true,
				msg.EventStreamId, msg.ExpectedVersion, msg.Events, msg.User));

	public void Handle(ClientMessage.DeleteStream msg) =>
		externalRequestQueue.Publish(
			new ClientMessage.DeleteStream(
				msg.InternalCorrId, msg.CorrelationId, new PublishToWrapEnvelop(inputQueue, msg.Envelope), true,
				msg.EventStreamId, msg.ExpectedVersion, msg.HardDelete, msg.User));

	// Historically, message forwarding here has discarded the original Expiration value,
	// resetting it to the default (10 seconds from now). Ideally, we should propagate the
	// original Expiration from the source message.
	//
	// For now, this fix takes a minimal-impact approach: we only preserve the Expiration
	// when explicitly needed (i.e., DateTime.MaxValue), and only for the relevant messages.

	public void Handle(ClientMessage.ReadStreamEventsBackward msg) =>
		externalRequestQueue.Publish(
			new ClientMessage.ReadStreamEventsBackward(
				msg.InternalCorrId, msg.CorrelationId, new PublishToWrapEnvelop(inputQueue, msg.Envelope),
				msg.EventStreamId, msg.FromEventNumber, msg.MaxCount, msg.ResolveLinkTos, msg.RequireLeader,
				msg.ValidationStreamVersion, msg.User,
				expires: msg.Expires == DateTime.MaxValue ? msg.Expires : null));

	public void Handle(ClientMessage.ReadStreamEventsForward msg) =>
		externalRequestQueue.Publish(
			new ClientMessage.ReadStreamEventsForward(
				msg.InternalCorrId, msg.CorrelationId, new PublishToWrapEnvelop(inputQueue, msg.Envelope),
				msg.EventStreamId, msg.FromEventNumber, msg.MaxCount, msg.ResolveLinkTos, msg.RequireLeader,
				msg.ValidationStreamVersion, msg.User, replyOnExpired: false,
				expires: msg.Expires == DateTime.MaxValue ? msg.Expires : null));

	public void Handle(ClientMessage.ReadAllEventsForward msg) =>
		externalRequestQueue.Publish(
			new ClientMessage.ReadAllEventsForward(
				msg.InternalCorrId, msg.CorrelationId, new PublishToWrapEnvelop(inputQueue, msg.Envelope),
				msg.CommitPosition, msg.PreparePosition, msg.MaxCount, msg.ResolveLinkTos, msg.RequireLeader,
				msg.ValidationTfLastCommitPosition, msg.User, replyOnExpired: false));

	public void Handle(SystemMessage.SubSystemInitialized msg) =>
		externalRequestQueue.Publish(
			new SystemMessage.SubSystemInitialized(msg.SubSystemName));

	void IHandle<ProjectionCoreServiceMessage.SubComponentStarted>.Handle(
		ProjectionCoreServiceMessage.SubComponentStarted message) =>
		externalRequestQueue.Publish(
			new ProjectionCoreServiceMessage.SubComponentStarted(message.SubComponent, message.InstanceCorrelationId)
		);

	void IHandle<ProjectionCoreServiceMessage.SubComponentStopped>.Handle(
		ProjectionCoreServiceMessage.SubComponentStopped message) =>
		externalRequestQueue.Publish(
			new ProjectionCoreServiceMessage.SubComponentStopped(message.SubComponent, message.QueueId)
		);
}
