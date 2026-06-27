using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messages;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed class IndexingService : IAsyncHandle<SystemMessage.SystemReady>, IAsyncHandle<SystemMessage.BecomeShuttingDown>, IAsyncDisposable
{
	private readonly ISubscriber _subscriber;
	private readonly IndexingSubscription _subscription;
	private int _disposed;

	public IndexingService(
		IIndexingComponent component,
		IIndexingEventSourceFactory eventSourceFactory,
		ISubscriber subscriber,
		IndexingSubscriptionOptions options)
	{
		_subscriber = subscriber;
		_subscription = new IndexingSubscription(component, eventSourceFactory, options);

		subscriber.Subscribe<SystemMessage.SystemReady>(this);
		subscriber.Subscribe<SystemMessage.BecomeShuttingDown>(this);
	}

	public ValueTask HandleAsync(SystemMessage.SystemReady message, CancellationToken token) =>
		_subscription.Start(token);

	public ValueTask HandleAsync(SystemMessage.BecomeShuttingDown message, CancellationToken token) =>
		DisposeAsync();

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) is not 0)
		{
			return;
		}

		try
		{
			await _subscription.DisposeAsync();
		}
		finally
		{
			_subscriber.Unsubscribe<SystemMessage.SystemReady>(this);
			_subscriber.Unsubscribe<SystemMessage.BecomeShuttingDown>(this);
		}
	}
}
