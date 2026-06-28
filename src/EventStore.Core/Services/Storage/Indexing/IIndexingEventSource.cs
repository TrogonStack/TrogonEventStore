using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Services.Transport.Common;
using EventStore.Core.Services.Transport.Enumerators;
using EventStore.Core.Services.UserManagement;

namespace EventStore.Core.Services.Storage.Indexing;

public interface IIndexingEventSource : IAsyncDisposable
{
	ReadResponse Current { get; }

	ValueTask<bool> MoveNextAsync();
}

public interface IIndexingEventSourceFactory
{
	IIndexingEventSource Create(IndexCheckpoint? checkpoint, CancellationToken token);
}

public sealed class AllStreamsIndexingEventSourceFactory : IIndexingEventSourceFactory
{
	private readonly IPublisher _publisher;

	public AllStreamsIndexingEventSourceFactory(IPublisher publisher) =>
		_publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));

	public IIndexingEventSource Create(IndexCheckpoint? checkpoint, CancellationToken token)
	{
		var subscription = new Enumerator.AllSubscription(
			_publisher,
			new DefaultExpiryStrategy(),
			checkpoint?.ToPosition(),
			resolveLinks: false,
			SystemAccounts.System,
			requiresLeader: false,
			token);

		return new AllStreamsIndexingEventSource(subscription);
	}

	private sealed class AllStreamsIndexingEventSource(Enumerator.AllSubscription subscription) : IIndexingEventSource
	{
		public ReadResponse Current => subscription.Current;

		public ValueTask<bool> MoveNextAsync() => subscription.MoveNextAsync();

		public ValueTask DisposeAsync() => subscription.DisposeAsync();
	}
}
