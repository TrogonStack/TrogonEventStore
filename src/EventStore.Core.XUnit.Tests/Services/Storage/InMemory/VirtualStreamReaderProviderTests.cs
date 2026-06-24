using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Messages;
using EventStore.Core.Services.Storage.InMemory;
using EventStore.Plugins;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.InMemory;

public class VirtualStreamReaderProviderTests
{
	[Fact]
	public void returns_no_readers_when_no_provider_components_are_configured()
	{
		var readers = new ClusterVNodeOptions()
			.PlugableComponents
			.OfType<IVirtualStreamReaderProvider>()
			.GetVirtualStreamReaders();

		Assert.Empty(readers);
	}

	[Fact]
	public void collects_readers_from_provider_components_in_registration_order()
	{
		var first = new FakeVirtualStreamReader("$mem-first");
		var second = new FakeVirtualStreamReader("$mem-second");
		var third = new FakeVirtualStreamReader("$mem-third");

		var options = new ClusterVNodeOptions()
			.WithPlugableComponent(new FakeComponent("regular"))
			.WithPlugableComponent(new FakeProvider("first-provider", first, second))
			.WithPlugableComponent(new FakeProvider("second-provider", third));

		var readers = options
			.PlugableComponents
			.OfType<IVirtualStreamReaderProvider>()
			.GetVirtualStreamReaders();

		Assert.Equal([first, second, third], readers);
	}

	private sealed class FakeComponent(string name) : Plugin(name);

	private sealed class FakeProvider(string name, params IVirtualStreamReader[] readers)
		: Plugin(name), IVirtualStreamReaderProvider
	{
		public IReadOnlyList<IVirtualStreamReader> VirtualStreamReaders { get; } = readers;
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
