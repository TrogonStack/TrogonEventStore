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
	private readonly object _registrationLock = new();
	private int _disposed;
	private int _started;
	private bool _registered;

	public IndexingService(
		IIndexingComponent component,
		IIndexingEventSourceFactory eventSourceFactory,
		ISubscriber subscriber,
		IndexingSubscriptionOptions options)
	{
		ArgumentNullException.ThrowIfNull(component);
		ArgumentNullException.ThrowIfNull(eventSourceFactory);
		ArgumentNullException.ThrowIfNull(subscriber);
		ArgumentNullException.ThrowIfNull(options);

		_subscriber = subscriber;
		_subscription = new IndexingSubscription(component, eventSourceFactory, options);
	}

	public void Register()
	{
		lock (_registrationLock)
		{
			ObjectDisposedException.ThrowIf(_disposed is not 0, this);
			if (_registered)
			{
				return;
			}

			_subscriber.Subscribe<SystemMessage.SystemReady>(this);
			_subscriber.Subscribe<SystemMessage.BecomeShuttingDown>(this);
			_registered = true;
		}
	}

	public async ValueTask HandleAsync(SystemMessage.SystemReady message, CancellationToken token)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) is not 0, this);

		if (Interlocked.CompareExchange(ref _started, 1, 0) is not 0)
		{
			return;
		}

		try
		{
			await _subscription.Start(token);
		}
		catch
		{
			Volatile.Write(ref _started, 0);
			throw;
		}
	}

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
			var registered = false;
			lock (_registrationLock)
			{
				if (_registered)
				{
					_registered = false;
					registered = true;
				}
			}

			if (registered)
			{
				_subscriber.Unsubscribe<SystemMessage.SystemReady>(this);
				_subscriber.Unsubscribe<SystemMessage.BecomeShuttingDown>(this);
			}
		}
	}
}
