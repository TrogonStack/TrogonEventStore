using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Services.Storage.Indexing;
using EventStore.Core.TransactionLog.LogRecords;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class IndexCheckpointWriterTests
{
	[Fact]
	public async Task read_delegates_to_store()
	{
		var expected = new IndexCheckpoint(10, 5);
		var store = new FakeIndexCheckpointStore { Checkpoint = expected };
		var writer = new IndexCheckpointWriter(store);

		var checkpoint = await writer.Read(CancellationToken.None);

		Assert.Equal(expected, checkpoint);
		Assert.Equal(1, store.ReadCalls);
	}

	[Fact]
	public async Task read_seeds_latest_checkpoint()
	{
		var store = new FakeIndexCheckpointStore { Checkpoint = new IndexCheckpoint(20, 15) };
		var writer = new IndexCheckpointWriter(store);

		await writer.Read(CancellationToken.None);

		var exception = Assert.Throws<InvalidOperationException>(() =>
			writer.Track(CreateResolvedEvent(commitPosition: 10, preparePosition: 5)));

		Assert.Contains("backwards", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task commit_before_tracking_is_no_op()
	{
		var store = new FakeIndexCheckpointStore();
		var writer = new IndexCheckpointWriter(store);

		await writer.Commit(CancellationToken.None);

		Assert.Equal(0, store.WriteCalls);
		Assert.Null(await store.Read(CancellationToken.None));
	}

	[Fact]
	public async Task tracking_then_commit_writes_commit_and_prepare_positions()
	{
		var store = new InMemoryIndexCheckpointStore();
		var writer = new IndexCheckpointWriter(store);

		writer.Track(CreateResolvedEvent(commitPosition: 20, preparePosition: 15));
		await writer.Commit(CancellationToken.None);

		var checkpoint = await store.Read(CancellationToken.None);

		Assert.Equal(new IndexCheckpoint(20, 15), checkpoint);
	}

	[Fact]
	public async Task later_tracked_positions_win()
	{
		var store = new InMemoryIndexCheckpointStore();
		var writer = new IndexCheckpointWriter(store);

		writer.Track(CreateResolvedEvent(commitPosition: 10, preparePosition: 5));
		writer.Track(CreateResolvedEvent(commitPosition: 20, preparePosition: 15));
		await writer.Commit(CancellationToken.None);

		var checkpoint = await store.Read(CancellationToken.None);

		Assert.Equal(new IndexCheckpoint(20, 15), checkpoint);
	}

	[Fact]
	public void track_without_original_position_throws()
	{
		var writer = new IndexCheckpointWriter(new InMemoryIndexCheckpointStore());
		var resolvedEvent = CreateResolvedEvent(
			commitPosition: 10,
			preparePosition: 5,
			isSelfCommitted: false).WithoutPosition();

		var exception = Assert.Throws<InvalidOperationException>(() => writer.Track(resolvedEvent));

		Assert.Contains("original position", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void constructor_rejects_null_store()
	{
		var exception = Assert.Throws<ArgumentNullException>(() => new IndexCheckpointWriter(null));

		Assert.Equal("store", exception.ParamName);
	}

	[Fact]
	public async Task read_passes_cancellation_token_to_store()
	{
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();
		var store = new FakeIndexCheckpointStore { CancelRead = true };
		var writer = new IndexCheckpointWriter(store);

		await Assert.ThrowsAsync<OperationCanceledException>(() => writer.Read(cancellation.Token).AsTask());

		Assert.Equal(cancellation.Token, store.LastReadToken);
	}

	[Fact]
	public async Task commit_passes_cancellation_token_to_store()
	{
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();
		var store = new FakeIndexCheckpointStore { CancelWrite = true };
		var writer = new IndexCheckpointWriter(store);

		writer.Track(CreateResolvedEvent(commitPosition: 10, preparePosition: 5));

		await Assert.ThrowsAsync<OperationCanceledException>(() => writer.Commit(cancellation.Token).AsTask());

		Assert.Equal(cancellation.Token, store.LastWriteToken);
	}

	[Fact]
	public void track_rejects_regressive_pending_position()
	{
		var writer = new IndexCheckpointWriter(new InMemoryIndexCheckpointStore());

		writer.Track(CreateResolvedEvent(commitPosition: 20, preparePosition: 15));

		var exception = Assert.Throws<InvalidOperationException>(() =>
			writer.Track(CreateResolvedEvent(commitPosition: 10, preparePosition: 5)));

		Assert.Contains("backwards", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task tracking_equal_position_is_idempotent_and_commits_once()
	{
		var store = new FakeIndexCheckpointStore();
		var writer = new IndexCheckpointWriter(store);

		writer.Track(CreateResolvedEvent(commitPosition: 10, preparePosition: 5));
		writer.Track(CreateResolvedEvent(commitPosition: 10, preparePosition: 5));

		await writer.Commit(CancellationToken.None);
		await writer.Commit(CancellationToken.None);

		Assert.Equal(1, store.WriteCalls);
		Assert.Equal(new IndexCheckpoint(10, 5), store.Checkpoint);
	}

	[Fact]
	public async Task commit_failure_leaves_pending_for_retry()
	{
		var store = new FakeIndexCheckpointStore { FailWrite = true };
		var writer = new IndexCheckpointWriter(store);

		writer.Track(CreateResolvedEvent(commitPosition: 10, preparePosition: 5));

		await Assert.ThrowsAsync<InvalidOperationException>(() => writer.Commit(CancellationToken.None).AsTask());

		Assert.Equal(1, store.WriteCalls);

		store.FailWrite = false;

		await writer.Commit(CancellationToken.None);

		Assert.Equal(2, store.WriteCalls);
		Assert.Equal(new IndexCheckpoint(10, 5), store.Checkpoint);
	}

	[Fact]
	public async Task successful_commit_clears_pending_so_second_commit_is_no_op()
	{
		var store = new FakeIndexCheckpointStore();
		var writer = new IndexCheckpointWriter(store);

		writer.Track(CreateResolvedEvent(commitPosition: 10, preparePosition: 5));

		await writer.Commit(CancellationToken.None);
		await writer.Commit(CancellationToken.None);

		Assert.Equal(1, store.WriteCalls);
		Assert.Equal(new IndexCheckpoint(10, 5), store.Checkpoint);
	}

	[Fact]
	public async Task successful_commit_prevents_later_regression()
	{
		var writer = new IndexCheckpointWriter(new InMemoryIndexCheckpointStore());

		writer.Track(CreateResolvedEvent(commitPosition: 20, preparePosition: 15));
		await writer.Commit(CancellationToken.None);

		var exception = Assert.Throws<InvalidOperationException>(() =>
			writer.Track(CreateResolvedEvent(commitPosition: 10, preparePosition: 5)));

		Assert.Contains("backwards", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task tracking_higher_position_during_async_commit_preserves_pending_for_next_commit()
	{
		var store = new BlockingIndexCheckpointStore();
		var writer = new IndexCheckpointWriter(store);

		writer.Track(CreateResolvedEvent(commitPosition: 10, preparePosition: 5));

		var commitTask = writer.Commit(CancellationToken.None).AsTask();
		await store.WaitForWriteStarted();

		writer.Track(CreateResolvedEvent(commitPosition: 20, preparePosition: 15));

		store.ReleaseWrite();
		await commitTask;

		Assert.Equal(1, store.WriteCalls);
		Assert.Equal(new IndexCheckpoint(10, 5), store.LastWritten);

		await writer.Commit(CancellationToken.None);

		Assert.Equal(2, store.WriteCalls);
		Assert.Equal(new IndexCheckpoint(20, 15), store.LastWritten);

		await writer.Commit(CancellationToken.None);

		Assert.Equal(2, store.WriteCalls);
	}

	private static ResolvedEvent CreateResolvedEvent(long commitPosition, long preparePosition, bool isSelfCommitted = true)
	{
		var flags = PrepareFlags.SingleWrite | PrepareFlags.Data;
		if (isSelfCommitted)
		{
			flags |= PrepareFlags.IsCommitted;
		}

		var record = new EventRecord(
			eventNumber: 0,
			logPosition: preparePosition,
			correlationId: Guid.NewGuid(),
			eventId: Guid.NewGuid(),
			transactionPosition: commitPosition,
			transactionOffset: 0,
			eventStreamId: "stream-1",
			expectedVersion: -1,
			timeStamp: DateTime.UtcNow,
			flags: flags,
			eventType: "event-type",
			data: Array.Empty<byte>(),
			metadata: Array.Empty<byte>());

		return ResolvedEvent.ForUnresolvedEvent(record, commitPosition);
	}

	private sealed class FakeIndexCheckpointStore : IIndexCheckpointStore
	{
		public IndexCheckpoint? Checkpoint { get; set; }
		public bool CancelRead { get; init; }
		public bool CancelWrite { get; init; }
		public bool FailWrite { get; set; }
		public int ReadCalls { get; private set; }
		public int WriteCalls { get; private set; }
		public CancellationToken LastReadToken { get; private set; }
		public CancellationToken LastWriteToken { get; private set; }

		public ValueTask<IndexCheckpoint?> Read(CancellationToken token)
		{
			ReadCalls++;
			LastReadToken = token;

			if (CancelRead)
			{
				token.ThrowIfCancellationRequested();
			}

			return ValueTask.FromResult(Checkpoint);
		}

		public ValueTask Write(IndexCheckpoint checkpoint, CancellationToken token)
		{
			WriteCalls++;
			LastWriteToken = token;

			if (FailWrite)
			{
				throw new InvalidOperationException("Simulated index checkpoint write failure.");
			}

			Checkpoint = checkpoint;

			if (CancelWrite)
			{
				token.ThrowIfCancellationRequested();
			}

			return ValueTask.CompletedTask;
		}
	}

	private sealed class BlockingIndexCheckpointStore : IIndexCheckpointStore
	{
		private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _releaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public IndexCheckpoint? LastWritten { get; private set; }
		public int WriteCalls { get; private set; }

		public ValueTask<IndexCheckpoint?> Read(CancellationToken token) =>
			ValueTask.FromResult(LastWritten);

		public async ValueTask Write(IndexCheckpoint checkpoint, CancellationToken token)
		{
			WriteCalls++;
			_writeStarted.TrySetResult();

			await _releaseWrite.Task;

			LastWritten = checkpoint;
		}

		public Task WaitForWriteStarted() => _writeStarted.Task;

		public void ReleaseWrite() => _releaseWrite.TrySetResult();
	}
}
