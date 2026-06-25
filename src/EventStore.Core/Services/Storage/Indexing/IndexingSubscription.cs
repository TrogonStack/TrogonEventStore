using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Transport.Enumerators;
using Serilog;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed record IndexingSubscriptionOptions
{
	public static readonly IndexingSubscriptionOptions Default = new(50000, TimeSpan.FromSeconds(10));

	public int CheckpointCommitBatchSize { get; }
	public TimeSpan CheckpointCommitDelay { get; }

	public IndexingSubscriptionOptions(int checkpointCommitBatchSize, TimeSpan checkpointCommitDelay)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointCommitBatchSize);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(checkpointCommitDelay, TimeSpan.Zero);

		CheckpointCommitBatchSize = checkpointCommitBatchSize;
		CheckpointCommitDelay = checkpointCommitDelay;
	}
}

public sealed class IndexingSubscription(
	IIndexingComponent component,
	IIndexingEventSourceFactory eventSourceFactory,
	IndexingSubscriptionOptions options) : IAsyncDisposable
{
	private static readonly ILogger Log = Serilog.Log.ForContext<IndexingSubscription>();

	private readonly CancellationTokenSource _stop = new();
	private readonly object _stateLock = new();

	private IIndexingEventSource _eventSource;
	private IndexCheckpointCommitTracker _commitTracker;
	private Task _processing;
	private bool _started;
	private bool _disposed;

	public async ValueTask Start(CancellationToken token)
	{
		lock (_stateLock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_started)
			{
				throw new InvalidOperationException($"{nameof(IndexingSubscription)} has already been started.");
			}

			_started = true;
		}

		using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);

		await component.Initialize(linked.Token);
		var checkpoint = await component.ReadCheckpoint(linked.Token);

		_commitTracker = new IndexCheckpointCommitTracker(
			options.CheckpointCommitBatchSize,
			options.CheckpointCommitDelay,
			component.Processor.Commit,
			_stop.Token);

		_eventSource = eventSourceFactory.Create(checkpoint, _stop.Token);
		_processing = Task.Run(ProcessEvents);
	}

	public async ValueTask DisposeAsync()
	{
		Task processing;
		IIndexingEventSource eventSource;
		IndexCheckpointCommitTracker commitTracker;

		lock (_stateLock)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			processing = _processing;
			eventSource = _eventSource;
			commitTracker = _commitTracker;
		}

		await _stop.CancelAsync();

		if (processing is not null)
		{
			try
			{
				await processing;
			}
			catch (OperationCanceledException)
			{
			}
		}

		if (commitTracker is not null)
		{
			await commitTracker.DisposeAsync();
		}

		if (eventSource is not null)
		{
			await eventSource.DisposeAsync();
		}

		await component.DisposeAsync();
		_stop.Dispose();
	}

	private async Task ProcessEvents()
	{
		while (!_stop.IsCancellationRequested)
		{
			bool hasNext;
			try
			{
				hasNext = await _eventSource.MoveNextAsync();
			}
			catch (OperationCanceledException) when (_stop.IsCancellationRequested)
			{
				return;
			}

			if (!hasNext)
			{
				return;
			}

			if (_eventSource.Current is not ReadResponse.EventReceived eventReceived)
			{
				continue;
			}

			try
			{
				await component.Processor.Index(eventReceived.Event, _stop.Token);
				_commitTracker.Track();
			}
			catch (OperationCanceledException) when (_stop.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Error while indexing event {eventType}", eventReceived.Event.Event.EventType);
				throw;
			}
		}
	}
}
