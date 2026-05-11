using EventStore.Core.Bus;
using EventStore.Core.Messaging;

namespace EventStore.Projections.Core.Messaging;

class PublishToWrapEnvelop : IEnvelope
{
	private readonly IPublisher _publisher;
	private readonly IEnvelope _nestedEnvelope;
	private readonly string _extraInformation;

	public PublishToWrapEnvelop(IPublisher publisher, IEnvelope nestedEnvelope, string extraInformation)
	{
		_publisher = publisher;
		_nestedEnvelope = nestedEnvelope;
		_extraInformation = extraInformation;
	}

	public void ReplyWith<T>(T message) where T : Message
	{
		_publisher.Publish(new UnwrapEnvelopeMessage(() => _nestedEnvelope.ReplyWith(message), _extraInformation));
	}
}
