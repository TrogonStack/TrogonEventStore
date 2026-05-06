using EventStore.Core.Bus;

namespace EventStore.Core.Authentication;

public record AuthenticationProviderFactoryComponents {
	public IPublisher MainQueue { get; init; }
	public ISubscriber MainBus { get; init; }
	public IPublisher WorkersQueue { get; init; }
	public InMemoryBus[] WorkerBuses { get; init; }
}
