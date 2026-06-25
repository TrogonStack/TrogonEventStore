using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed class IndexCheckpointCommitTracker : IAsyncDisposable
{
	private readonly int _batchSize;
	private readonly TimeSpan _maxCommitDelay;
	private readonly Func<CancellationToken, ValueTask> _commit;
	private readonly CancellationTokenSource _lifetime;
	private readonly SemaphoreSlim _commitRequested = new(0, 1);
	private readonly Task _runTask;

	private int _disposed;
	private int _pending;

	public IndexCheckpointCommitTracker(
		int batchSize,
		TimeSpan maxCommitDelay,
		Func<CancellationToken, ValueTask> commit,
		CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxCommitDelay, TimeSpan.Zero);

		_batchSize = batchSize;
		_maxCommitDelay = maxCommitDelay;
		_commit = commit ?? throw new ArgumentNullException(nameof(commit));
		_lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_runTask = Task.Run(RunAsync);
	}

	public void Track()
	{
		ObjectDisposedException.ThrowIf(_disposed is not 0 || _lifetime.IsCancellationRequested, this);

		if (Interlocked.Increment(ref _pending) == _batchSize)
			RequestCommit();
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) is not 0)
			return;

		await _lifetime.CancelAsync();

		try
		{
			await _runTask;
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			_commitRequested.Dispose();
			_lifetime.Dispose();
		}
	}

	private async Task RunAsync()
	{
		var token = _lifetime.Token;

		while (!token.IsCancellationRequested)
		{
			try
			{
				await _commitRequested.WaitAsync(_maxCommitDelay, token);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				break;
			}

			var pending = Interlocked.Exchange(ref _pending, 0);
			if (pending is 0)
				continue;

			try
			{
				await _commit(token);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				Interlocked.Add(ref _pending, pending);
				Log.Error(ex, "Error while committing an index checkpoint");
			}
		}
	}

	private void RequestCommit()
	{
		try
		{
			_commitRequested.Release();
		}
		catch (SemaphoreFullException)
		{
		}
	}
}
