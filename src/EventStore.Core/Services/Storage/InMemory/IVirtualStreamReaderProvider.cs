using System.Collections.Generic;
using System.Linq;

namespace EventStore.Core.Services.Storage.InMemory;

public interface IVirtualStreamReaderProvider
{
	IReadOnlyList<IVirtualStreamReader> VirtualStreamReaders { get; }
}

public static class VirtualStreamReaderProviderExtensions
{
	public static IReadOnlyList<IVirtualStreamReader> GetVirtualStreamReaders(
		this IEnumerable<IVirtualStreamReaderProvider> providers) =>
		providers.SelectMany(provider => provider.VirtualStreamReaders).ToArray();
}
