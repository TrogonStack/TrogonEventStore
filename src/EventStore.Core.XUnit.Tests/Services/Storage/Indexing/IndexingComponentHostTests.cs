using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Storage.Indexing;
using EventStore.Core.Services.Storage.InMemory;
using EventStore.Core.Services.Transport.Enumerators;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class IndexingComponentHostTests
{
	[Fact]
	public void constructor_rejects_component_with_missing_virtual_stream_readers()
	{
		var exception = Assert.Throws<ArgumentException>(() =>
			new IndexingComponentHost(new MissingVirtualStreamReadersComponent()));

		Assert.Equal("components", exception.ParamName);
		Assert.Equal("Indexing component virtual stream readers cannot be null. (Parameter 'components')", exception.Message);
	}

	[Fact]
	public void constructor_rejects_component_with_missing_virtual_stream_reader()
	{
		var exception = Assert.Throws<ArgumentException>(() =>
			new IndexingComponentHost(new FakeIndexingComponent([null!])));

		Assert.Equal("components", exception.ParamName);
		Assert.Equal("Indexing component virtual stream readers cannot contain null. (Parameter 'components')", exception.Message);
	}

	[Fact]
	public void exposes_component_virtual_stream_readers()
	{
		var first = new FakeVirtualStreamReader("$idx-first");
		var second = new FakeVirtualStreamReader("$idx-second");
		var component = new FakeIndexingComponent(first, second);
		var host = new IndexingComponentHost(component);

		Assert.Equal([first, second], host.VirtualStreamReaders);
	}

	[Fact]
	public void exposes_grouped_component_virtual_stream_readers()
	{
		var first = new FakeVirtualStreamReader("$idx-first");
		var second = new FakeVirtualStreamReader("$idx-second");
		var firstComponent = new FakeIndexingComponent(first);
		var secondComponent = new FakeIndexingComponent(second);
		var host = new IndexingComponentHost([firstComponent, secondComponent]);

		Assert.Equal([first, second], host.VirtualStreamReaders);
	}

	[Fact]
	public async Task configure_application_activates_registered_indexing_service()
	{
		var subscriber = new RecordingSubscriber();
		var services = new ServiceCollection();
		var component = new FakeIndexingComponent();
		var host = new IndexingComponentHost(component);
		var configuration = new ConfigurationBuilder().Build();

		services.AddSingleton<ISubscriber>(subscriber);
		services.AddSingleton<IPublisher>(new RecordingPublisher());
		host.ConfigureServices(services, configuration);

		await using var provider = services.BuildServiceProvider();
		_ = provider.GetRequiredService<IndexingService>();

		Assert.False(subscriber.Has<SystemMessage.SystemReady>());
		Assert.False(subscriber.Has<SystemMessage.BecomeShuttingDown>());

		host.ConfigureApplication(new ApplicationBuilder(provider), configuration);

		Assert.True(subscriber.Has<SystemMessage.SystemReady>());
		Assert.True(subscriber.Has<SystemMessage.BecomeShuttingDown>());
	}

	[Fact]
	public async Task configure_application_activates_grouped_indexing_services()
	{
		var subscriber = new RecordingSubscriber();
		var services = new ServiceCollection();
		var first = new FakeIndexingComponent();
		var second = new FakeIndexingComponent();
		var host = new IndexingComponentHost([first, second]);
		var configuration = new ConfigurationBuilder().Build();

		services.AddSingleton<ISubscriber>(subscriber);
		services.AddSingleton<IPublisher>(new RecordingPublisher());
		host.ConfigureServices(services, configuration);

		await using var provider = services.BuildServiceProvider();
		Assert.Equal(2, provider.GetServices<IndexingService>().Count());

		host.ConfigureApplication(new ApplicationBuilder(provider), configuration);

		Assert.Equal(2, subscriber.SubscriptionCount<SystemMessage.SystemReady>());
		Assert.Equal(2, subscriber.SubscriptionCount<SystemMessage.BecomeShuttingDown>());
	}

	[Fact]
	public async Task configure_application_can_register_indexing_service_more_than_once()
	{
		var subscriber = new RecordingSubscriber();
		var services = new ServiceCollection();
		var component = new FakeIndexingComponent();
		var host = new IndexingComponentHost(component);
		var configuration = new ConfigurationBuilder().Build();

		services.AddSingleton<ISubscriber>(subscriber);
		services.AddSingleton<IPublisher>(new RecordingPublisher());
		host.ConfigureServices(services, configuration);

		await using var provider = services.BuildServiceProvider();
		var application = new ApplicationBuilder(provider);

		host.ConfigureApplication(application, configuration);
		host.ConfigureApplication(application, configuration);

		Assert.Equal(1, subscriber.SubscriptionCount<SystemMessage.SystemReady>());
		Assert.Equal(1, subscriber.SubscriptionCount<SystemMessage.BecomeShuttingDown>());
	}

	private sealed class RecordingSubscriber : ISubscriber
	{
		private readonly Dictionary<Type, int> _subscriptions = [];

		public void Subscribe<T>(IAsyncHandle<T> handler) where T : Message =>
			_subscriptions[typeof(T)] = SubscriptionCount<T>() + 1;

		public void Unsubscribe<T>(IAsyncHandle<T> handler) where T : Message =>
			_subscriptions.Remove(typeof(T));

		public bool Has<T>() where T : Message => SubscriptionCount<T>() > 0;

		public int SubscriptionCount<T>() where T : Message =>
			_subscriptions.GetValueOrDefault(typeof(T));
	}

	private sealed class RecordingPublisher : IPublisher
	{
		public void Publish(Message message)
		{
		}
	}

	private sealed class FakeIndexingComponent(params IVirtualStreamReader[] virtualStreamReaders) : IIndexingComponent
	{
		public IIndexingProcessor Processor { get; } = new FakeIndexingProcessor();

		public IReadOnlyList<IVirtualStreamReader> VirtualStreamReaders { get; } = virtualStreamReaders;

		public ValueTask Initialize(CancellationToken token) => ValueTask.CompletedTask;

		public ValueTask<IndexCheckpoint?> ReadCheckpoint(CancellationToken token) => ValueTask.FromResult<IndexCheckpoint?>(null);

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class MissingVirtualStreamReadersComponent : IIndexingComponent
	{
		public IIndexingProcessor Processor { get; } = new FakeIndexingProcessor();

		public IReadOnlyList<IVirtualStreamReader> VirtualStreamReaders => null!;

		public ValueTask Initialize(CancellationToken token) => ValueTask.CompletedTask;

		public ValueTask<IndexCheckpoint?> ReadCheckpoint(CancellationToken token) => ValueTask.FromResult<IndexCheckpoint?>(null);

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class FakeIndexingProcessor : IIndexingProcessor
	{
		public ValueTask Index(ResolvedEvent resolvedEvent, CancellationToken token) => ValueTask.CompletedTask;

		public ValueTask Commit(CancellationToken token) => ValueTask.CompletedTask;
	}

	private sealed class FakeVirtualStreamReader(string streamId) : IVirtualStreamReader
	{
		public ValueTask<ClientMessage.ReadStreamEventsForwardCompleted> ReadForwards(
			ClientMessage.ReadStreamEventsForward msg,
			CancellationToken token) =>
			throw new NotSupportedException();

		public ValueTask<ClientMessage.ReadStreamEventsBackwardCompleted> ReadBackwards(
			ClientMessage.ReadStreamEventsBackward msg,
			CancellationToken token) =>
			throw new NotSupportedException();

		public long GetLastEventNumber(string streamId) => 0;

		public long GetLastIndexedPosition(string streamId) => 0;

		public bool CanReadStream(string candidateStreamId) => candidateStreamId == streamId;
	}
}
