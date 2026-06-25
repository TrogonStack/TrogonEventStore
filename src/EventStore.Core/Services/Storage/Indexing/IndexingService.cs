using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messages;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed class IndexingService : IAsyncHandle<SystemMessage.SystemReady>, IAsyncHandle<SystemMessage.BecomeShuttingDown>, IAsyncDisposable
{
	private readonly IndexingSubscription _subscription;

	public IndexingService(
		IIndexingComponent component,
		IIndexingEventSourceFactory eventSourceFactory,
		ISubscriber subscriber,
		IndexingSubscriptionOptions options)
	{
		_subscription = new IndexingSubscription(component, eventSourceFactory, options);

		subscriber.Subscribe<SystemMessage.SystemReady>(this);
		subscriber.Subscribe<SystemMessage.BecomeShuttingDown>(this);
	}

	public ValueTask HandleAsync(SystemMessage.SystemReady message, CancellationToken token) =>
		_subscription.Start(token);

	public ValueTask HandleAsync(SystemMessage.BecomeShuttingDown message, CancellationToken token) =>
		_subscription.DisposeAsync();

	public ValueTask DisposeAsync() => _subscription.DisposeAsync();
}
