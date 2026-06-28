using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Storage.Indexing;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class InMemoryIndexCheckpointStoreTests
{
	[Fact]
	public async Task read_returns_null_when_empty()
	{
		var store = new InMemoryIndexCheckpointStore();

		var checkpoint = await store.Read(CancellationToken.None);

		Assert.Null(checkpoint);
	}

	[Fact]
	public async Task write_and_read_round_trip()
	{
		var store = new InMemoryIndexCheckpointStore();
		var expected = new IndexCheckpoint(10, 5);

		await store.Write(expected, CancellationToken.None);
		var checkpoint = await store.Read(CancellationToken.None);

		Assert.Equal(expected, checkpoint);
	}

	[Fact]
	public async Task write_overwrites_stored_checkpoint()
	{
		var store = new InMemoryIndexCheckpointStore();
		var first = new IndexCheckpoint(10, 5);
		var second = new IndexCheckpoint(20, 15);

		await store.Write(first, CancellationToken.None);
		await store.Write(second, CancellationToken.None);
		var checkpoint = await store.Read(CancellationToken.None);

		Assert.Equal(second, checkpoint);
	}

	[Fact]
	public async Task read_honors_cancelled_token()
	{
		var store = new InMemoryIndexCheckpointStore();
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			store.Read(cancellation.Token).AsTask());
	}

	[Fact]
	public async Task write_honors_cancelled_token()
	{
		var store = new InMemoryIndexCheckpointStore();
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			store.Write(new IndexCheckpoint(10, 5), cancellation.Token).AsTask());
	}
}
