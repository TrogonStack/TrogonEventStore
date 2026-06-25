using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed class IndexCheckpointCommitTracker : IAsyncDisposable
{
	private readonly int _batchSize;
	private readonly TimeSpan _maxCommitDelay;
	private readonly Func<CancellationToken, ValueTask> _commit;
	private readonly object _stateLock = new();
	private readonly CancellationTokenSource _lifetime;
	private readonly CancellationTokenRegistration _stopTracking;
	private readonly SemaphoreSlim _commitRequested = new(0, 1);
	private readonly TaskCompletionSource _disposeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Task _runTask;

	private int _disposed;
	private int _pending;
	private bool _stopped;

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
		_stopTracking = _lifetime.Token.Register(static state =>
			((IndexCheckpointCommitTracker)state!).StopTracking(), this);
		_runTask = Task.Run(RunAsync);
	}

	public void Track()
	{
		lock (_stateLock)
		{
			ObjectDisposedException.ThrowIf(_stopped, this);

			if (Interlocked.Increment(ref _pending) >= _batchSize)
				RequestCommit();
		}
	}

	public async ValueTask DisposeAsync()
	{
		var dispose = false;
		lock (_stateLock)
		{
			if (_disposed is 0)
			{
				_disposed = 1;
				_stopped = true;
				dispose = true;
			}
		}

		if (!dispose)
		{
			await _disposeCompleted.Task;
			return;
		}

		Exception failure = null;

		try
		{
			StopTracking();
			await _lifetime.CancelAsync();

			try
			{
				await _runTask;
			}
			catch (OperationCanceledException)
			{
			}
		}
		catch (Exception ex)
		{
			failure = ex;
		}

		try
		{
			_stopTracking.Dispose();
			_commitRequested.Dispose();
			_lifetime.Dispose();
		}
		catch (Exception ex) when (failure is null)
		{
			failure = ex;
		}

		if (failure is null)
		{
			_disposeCompleted.SetResult();
			return;
		}

		_disposeCompleted.SetException(failure);
		ExceptionDispatchInfo.Capture(failure).Throw();
	}

	private async Task RunAsync()
	{
		var token = _lifetime.Token;

		try
		{
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

				try
				{
					await CommitPending(token, requestRetry: true);
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					break;
				}
			}
		}
		finally
		{
			StopTracking();
			await CommitPending(CancellationToken.None, requestRetry: false);
		}
	}

	private async ValueTask CommitPending(CancellationToken token, bool requestRetry)
	{
		var pending = Interlocked.Exchange(ref _pending, 0);
		if (pending is 0)
			return;

		try
		{
			await _commit(token);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			Interlocked.Add(ref _pending, pending);
			throw;
		}
		catch (Exception ex)
		{
			var restoredPending = Interlocked.Add(ref _pending, pending);
			if (!requestRetry)
			{
				Log.Error(ex, "Error while committing an index checkpoint");
				throw;
			}

			if (restoredPending >= _batchSize)
				RequestCommit();

			Log.Error(ex, "Error while committing an index checkpoint");
		}
	}

	private void StopTracking()
	{
		lock (_stateLock)
			_stopped = true;
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
