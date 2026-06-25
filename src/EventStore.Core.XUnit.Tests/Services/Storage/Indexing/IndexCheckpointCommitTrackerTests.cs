using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Storage.Indexing;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class IndexCheckpointCommitTrackerTests
{
	[Fact]
	public async Task commits_when_batch_size_is_reached()
	{
		var calls = 0;
		var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var tracker = new IndexCheckpointCommitTracker(
			batchSize: 5,
			maxCommitDelay: TimeSpan.FromSeconds(30),
			commit: _ =>
			{
				Interlocked.Increment(ref calls);
				committed.SetResult();
				return ValueTask.CompletedTask;
			},
			cancellationToken: CancellationToken.None);

		for (var index = 0; index < 5; index++)
			tracker.Track();

		await committed.Task.WaitAsync(CommitTimeout);

		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task commits_pending_changes_when_delay_elapses()
	{
		var calls = 0;
		var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var tracker = new IndexCheckpointCommitTracker(
			batchSize: 100,
			maxCommitDelay: TimeSpan.FromMilliseconds(25),
			commit: _ =>
			{
				Interlocked.Increment(ref calls);
				committed.SetResult();
				return ValueTask.CompletedTask;
			},
			cancellationToken: CancellationToken.None);

		tracker.Track();

		await committed.Task.WaitAsync(CommitTimeout);

		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task does_not_commit_when_no_changes_were_tracked()
	{
		var calls = 0;
		await using (new IndexCheckpointCommitTracker(
			batchSize: 100,
			maxCommitDelay: TimeSpan.FromMilliseconds(25),
			commit: _ =>
			{
				Interlocked.Increment(ref calls);
				return ValueTask.CompletedTask;
			},
			cancellationToken: CancellationToken.None))
		{
			await Task.Delay(TimeSpan.FromMilliseconds(75));
		}

		Assert.Equal(0, calls);
	}

	[Fact]
	public async Task does_not_run_commits_reentrantly()
	{
		var currentCommits = 0;
		var maxConcurrentCommits = 0;
		var firstCommitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var tracker = new IndexCheckpointCommitTracker(
			batchSize: 3,
			maxCommitDelay: TimeSpan.FromSeconds(30),
			commit: async _ =>
			{
				var active = Interlocked.Increment(ref currentCommits);
				SetMax(ref maxConcurrentCommits, active);
				firstCommitStarted.TrySetResult();

				await releaseCommit.Task;

				Interlocked.Decrement(ref currentCommits);
			},
			cancellationToken: CancellationToken.None);

		for (var index = 0; index < 3; index++)
			tracker.Track();

		await firstCommitStarted.Task.WaitAsync(CommitTimeout);

		for (var index = 0; index < 3; index++)
			tracker.Track();

		releaseCommit.SetResult();

		await Task.Delay(TimeSpan.FromMilliseconds(50));

		Assert.Equal(1, maxConcurrentCommits);
	}

	[Fact]
	public async Task concurrent_tracks_trigger_one_batch_commit()
	{
		var calls = 0;
		var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var tracker = new IndexCheckpointCommitTracker(
			batchSize: 100,
			maxCommitDelay: TimeSpan.FromSeconds(30),
			commit: _ =>
			{
				Interlocked.Increment(ref calls);
				committed.SetResult();
				return ValueTask.CompletedTask;
			},
			cancellationToken: CancellationToken.None);

		await Task.WhenAll(Enumerable
			.Range(0, 100)
			.Select(_ => Task.Run(tracker.Track)));

		await committed.Task.WaitAsync(CommitTimeout);

		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task failed_commit_keeps_pending_changes_for_retry()
	{
		var calls = 0;
		var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var tracker = new IndexCheckpointCommitTracker(
			batchSize: 1,
			maxCommitDelay: TimeSpan.FromSeconds(30),
			commit: _ =>
			{
				if (Interlocked.Increment(ref calls) == 1)
					throw new InvalidOperationException("transient failure");

				committed.SetResult();
				return ValueTask.CompletedTask;
			},
			cancellationToken: CancellationToken.None);

		tracker.Track();

		await committed.Task.WaitAsync(CommitTimeout);

		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task cancellation_stops_future_tracks()
	{
		var calls = 0;
		using var cancellation = new CancellationTokenSource();
		await using var tracker = new IndexCheckpointCommitTracker(
			batchSize: 1,
			maxCommitDelay: TimeSpan.FromSeconds(30),
			commit: _ =>
			{
				Interlocked.Increment(ref calls);
				return ValueTask.CompletedTask;
			},
			cancellationToken: cancellation.Token);

		await cancellation.CancelAsync();
		await Task.Delay(TimeSpan.FromMilliseconds(50));

		var exception = Assert.Throws<ObjectDisposedException>(tracker.Track);
		await Task.Delay(TimeSpan.FromMilliseconds(50));

		Assert.Contains(nameof(IndexCheckpointCommitTracker), exception.Message);
		Assert.Equal(0, calls);
	}

	[Fact]
	public async Task cancellation_commits_pending_changes_before_stopping()
	{
		var calls = 0;
		var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var cancellation = new CancellationTokenSource();
		await using var tracker = new IndexCheckpointCommitTracker(
			batchSize: 100,
			maxCommitDelay: TimeSpan.FromSeconds(30),
			commit: _ =>
			{
				Interlocked.Increment(ref calls);
				committed.SetResult();
				return ValueTask.CompletedTask;
			},
			cancellationToken: cancellation.Token);

		tracker.Track();
		await cancellation.CancelAsync();

		await committed.Task.WaitAsync(CommitTimeout);

		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task dispose_commits_pending_changes_before_stopping()
	{
		var calls = 0;
		var tracker = new IndexCheckpointCommitTracker(
			batchSize: 100,
			maxCommitDelay: TimeSpan.FromSeconds(30),
			commit: _ =>
			{
				Interlocked.Increment(ref calls);
				return ValueTask.CompletedTask;
			},
			cancellationToken: CancellationToken.None);

		tracker.Track();
		await tracker.DisposeAsync();

		Assert.Equal(1, calls);
	}

	[Fact]
	public async Task dispose_surfaces_pending_commit_failure()
	{
		var tracker = new IndexCheckpointCommitTracker(
			batchSize: 100,
			maxCommitDelay: TimeSpan.FromSeconds(30),
			commit: _ => throw new InvalidOperationException("commit failed"),
			cancellationToken: CancellationToken.None);

		tracker.Track();

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => tracker.DisposeAsync().AsTask());

		Assert.Equal("commit failed", exception.Message);
	}

	[Fact]
	public async Task track_after_dispose_throws()
	{
		var tracker = new IndexCheckpointCommitTracker(
			batchSize: 10,
			maxCommitDelay: TimeSpan.FromSeconds(30),
			commit: _ => ValueTask.CompletedTask,
			cancellationToken: CancellationToken.None);

		await tracker.DisposeAsync();

		var exception = Assert.Throws<ObjectDisposedException>(tracker.Track);
		Assert.Contains(nameof(IndexCheckpointCommitTracker), exception.Message);
	}

	[Fact]
	public async Task concurrent_dispose_calls_are_safe()
	{
		var commitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var tracker = new IndexCheckpointCommitTracker(
			batchSize: 1,
			maxCommitDelay: TimeSpan.FromSeconds(30),
			commit: async _ =>
			{
				commitStarted.SetResult();
				await releaseCommit.Task;
			},
			cancellationToken: CancellationToken.None);

		tracker.Track();
		await commitStarted.Task.WaitAsync(CommitTimeout);

		var disposeTasks = Enumerable
			.Range(0, 5)
			.Select(_ => Task.Run(async () => await tracker.DisposeAsync()))
			.ToArray();

		await Task.Delay(TimeSpan.FromMilliseconds(50));

		foreach (var disposeTask in disposeTasks)
			Assert.False(disposeTask.IsCompleted);

		releaseCommit.SetResult();
		await Task.WhenAll(disposeTasks).WaitAsync(CommitTimeout);
	}

	[Fact]
	public async Task dispose_waits_for_active_commit_to_finish()
	{
		var commitFinished = false;
		var commitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var tracker = new IndexCheckpointCommitTracker(
			batchSize: 1,
			maxCommitDelay: TimeSpan.FromSeconds(30),
			commit: async _ =>
			{
				commitStarted.SetResult();
				await releaseCommit.Task;
				commitFinished = true;
			},
			cancellationToken: CancellationToken.None);

		tracker.Track();
		await commitStarted.Task.WaitAsync(CommitTimeout);

		var disposeTask = tracker.DisposeAsync().AsTask();

		Assert.False(disposeTask.IsCompleted);

		releaseCommit.SetResult();
		await disposeTask.WaitAsync(CommitTimeout);

		Assert.True(commitFinished);
	}

	private static void SetMax(ref int target, int candidate)
	{
		while (true)
		{
			var current = Volatile.Read(ref target);
			if (current >= candidate)
				return;

			if (Interlocked.CompareExchange(ref target, candidate, current) == current)
				return;
		}
	}

	private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(5);
}
