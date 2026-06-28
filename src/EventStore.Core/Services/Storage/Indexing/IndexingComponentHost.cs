using System;
using System.Collections.Generic;
using System.Linq;
using EventStore.Core.Bus;
using EventStore.Core.Services.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed class IndexingComponentHost : IVirtualStreamReaderProvider
{
	private readonly IReadOnlyList<IIndexingComponent> _components;
	private readonly IReadOnlyList<IVirtualStreamReader> _virtualStreamReaders;
	private readonly IndexingSubscriptionOptions _options;

	public IndexingComponentHost(IIndexingComponent component)
		: this(component, IndexingSubscriptionOptions.Default)
	{
	}

	public IndexingComponentHost(IIndexingComponent component, IndexingSubscriptionOptions options)
		: this([component], options)
	{
	}

	public IndexingComponentHost(IReadOnlyList<IIndexingComponent> components)
		: this(components, IndexingSubscriptionOptions.Default)
	{
	}

	public IndexingComponentHost(IReadOnlyList<IIndexingComponent> components, IndexingSubscriptionOptions options)
	{
		ArgumentNullException.ThrowIfNull(components);
		ArgumentNullException.ThrowIfNull(options);

		var componentArray = components.ToArray();
		if (componentArray.Length == 0)
		{
			throw new ArgumentException("At least one indexing component is required.", nameof(components));
		}

		if (componentArray.Any(static component => component is null))
		{
			throw new ArgumentException("Indexing components cannot contain null.", nameof(components));
		}

		_components = componentArray;
		_options = options;
		_virtualStreamReaders = GetVirtualStreamReaders(componentArray);
	}

	public IReadOnlyList<IVirtualStreamReader> VirtualStreamReaders => _virtualStreamReaders;

	public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
	{
		foreach (var component in _components)
		{
			services.AddSingleton(serviceProvider => new IndexingService(
				component,
				new AllStreamsIndexingEventSourceFactory(serviceProvider.GetRequiredService<IPublisher>()),
				serviceProvider.GetRequiredService<ISubscriber>(),
				_options));
		}
	}

	public void ConfigureApplication(IApplicationBuilder builder, IConfiguration configuration)
	{
		foreach (var service in builder.ApplicationServices.GetServices<IndexingService>())
		{
			service.Register();
		}
	}

	private static IReadOnlyList<IVirtualStreamReader> GetVirtualStreamReaders(IReadOnlyList<IIndexingComponent> components)
	{
		var readers = components.SelectMany(static component =>
			component.VirtualStreamReaders
				?? throw new ArgumentException("Indexing component virtual stream readers cannot be null.", nameof(components)))
			.ToArray();

		if (readers.Any(static reader => reader is null))
		{
			throw new ArgumentException("Indexing component virtual stream readers cannot contain null.", nameof(components));
		}

		return readers;
	}
}
