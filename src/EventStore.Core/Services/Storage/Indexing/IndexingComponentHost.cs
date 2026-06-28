using System.Collections.Generic;
using EventStore.Core.Bus;
using EventStore.Core.Services.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed class IndexingComponentHost(
	IIndexingComponent component,
	IndexingSubscriptionOptions options) : IVirtualStreamReaderProvider
{
	public IndexingComponentHost(IIndexingComponent component)
		: this(component, IndexingSubscriptionOptions.Default)
	{
	}

	public IReadOnlyList<IVirtualStreamReader> VirtualStreamReaders => component.VirtualStreamReaders;

	public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
	{
		services.AddSingleton(serviceProvider => new IndexingService(
			component,
			new AllStreamsIndexingEventSourceFactory(serviceProvider.GetRequiredService<IPublisher>()),
			serviceProvider.GetRequiredService<ISubscriber>(),
			options));
	}

	public void ConfigureApplication(IApplicationBuilder builder, IConfiguration configuration)
	{
		// IndexingService subscribes to the bus from its constructor, so force singleton activation
		// while the application is being configured instead of waiting for the first service lookup.
		foreach (var service in builder.ApplicationServices.GetServices<IndexingService>())
		{
			_ = service;
		}
	}
}
