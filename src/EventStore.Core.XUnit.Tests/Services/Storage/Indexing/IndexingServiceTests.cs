using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Storage.Indexing;
using EventStore.Core.Services.Storage.InMemory;
using EventStore.Core.Services.Transport.Enumerators;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class IndexingServiceTests
{
	[Fact]
	public async Task shutdown_disposes_subscription_and_unsubscribes()
	{
		var subscriber = new RecordingSubscriber();
		var component = new FakeIndexingComponent();
		var eventSource = new FakeIndexingEventSource();
		var service = new IndexingService(
			component,
			new FakeIndexingEventSourceFactory(eventSource),
			subscriber,
			IndexingSubscriptionOptions.Default);

		service.Register();
		await service.HandleAsync(new SystemMessage.SystemReady(), CancellationToken.None);
		await service.HandleAsync(new SystemMessage.BecomeShuttingDown(Guid.NewGuid(), false, false), CancellationToken.None);

		Assert.True(component.Disposed);
		Assert.True(eventSource.Disposed);
		Assert.False(subscriber.Has<SystemMessage.SystemReady>());
		Assert.False(subscriber.Has<SystemMessage.BecomeShuttingDown>());
	}

	[Fact]
	public async Task dispose_unsubscribes_when_subscription_cleanup_fails()
	{
		var subscriber = new RecordingSubscriber();
		var service = new IndexingService(
			new FakeIndexingComponent(throwOnDispose: true),
			new FakeIndexingEventSourceFactory(new FakeIndexingEventSource()),
			subscriber,
			IndexingSubscriptionOptions.Default);

		service.Register();
		var exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.DisposeAsync().AsTask());

		Assert.Equal("dispose failed", exception.Message);
		Assert.False(subscriber.Has<SystemMessage.SystemReady>());
		Assert.False(subscriber.Has<SystemMessage.BecomeShuttingDown>());
	}

	private sealed class RecordingSubscriber : ISubscriber
	{
		private readonly HashSet<Type> _subscriptions = [];

		public void Subscribe<T>(IAsyncHandle<T> handler) where T : Message =>
			_subscriptions.Add(typeof(T));

		public void Unsubscribe<T>(IAsyncHandle<T> handler) where T : Message =>
			_subscriptions.Remove(typeof(T));

		public bool Has<T>() where T : Message => _subscriptions.Contains(typeof(T));
	}

	private sealed class FakeIndexingComponent(bool throwOnDispose = false) : IIndexingComponent
	{
		public IIndexingProcessor Processor { get; } = new FakeIndexingProcessor();

		public IReadOnlyList<IVirtualStreamReader> VirtualStreamReaders { get; } = [];

		public bool Disposed { get; private set; }

		public ValueTask Initialize(CancellationToken token) => ValueTask.CompletedTask;

		public ValueTask<IndexCheckpoint?> ReadCheckpoint(CancellationToken token) => ValueTask.FromResult<IndexCheckpoint?>(null);

		public ValueTask DisposeAsync()
		{
			Disposed = true;
			return throwOnDispose
				? ValueTask.FromException(new InvalidOperationException("dispose failed"))
				: ValueTask.CompletedTask;
		}
	}

	private sealed class FakeIndexingProcessor : IIndexingProcessor
	{
		public ValueTask Index(ResolvedEvent resolvedEvent, CancellationToken token) => ValueTask.CompletedTask;

		public ValueTask Commit(CancellationToken token) => ValueTask.CompletedTask;
	}

	private sealed class FakeIndexingEventSourceFactory(FakeIndexingEventSource source) : IIndexingEventSourceFactory
	{
		public IIndexingEventSource Create(IndexCheckpoint? checkpoint, CancellationToken token)
		{
			source.Bind(token);
			return source;
		}
	}

	private sealed class FakeIndexingEventSource : IIndexingEventSource
	{
		private CancellationToken _token;

		public ReadResponse Current => null;

		public bool Disposed { get; private set; }

		public void Bind(CancellationToken token) => _token = token;

		public async ValueTask<bool> MoveNextAsync()
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, _token);
			return false;
		}

		public ValueTask DisposeAsync()
		{
			Disposed = true;
			return ValueTask.CompletedTask;
		}
	}
}
