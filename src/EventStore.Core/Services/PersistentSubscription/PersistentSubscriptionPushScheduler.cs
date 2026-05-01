using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;

namespace EventStore.Core.Services.PersistentSubscription {
	public class PersistentSubscriptionPushScheduler : IPersistentSubscriptionPushScheduler {
		private readonly IPublisher _publisher;
		private readonly Message _message;

		public PersistentSubscriptionPushScheduler(string subscriptionId, IPublisher publisher) {
			_publisher = publisher;
			_message = new SubscriptionMessage.PersistentSubscriptionPushToClients(subscriptionId);
		}

		public void SchedulePush() {
			_publisher.Publish(_message);
		}
	}
}
