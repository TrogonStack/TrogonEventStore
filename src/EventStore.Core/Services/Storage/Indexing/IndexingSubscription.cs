using System;
using System.Runtime.ExceptionServices;
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

public sealed class IndexingSubscription : IAsyncDisposable
{
	private static readonly ILogger Log = Serilog.Log.ForContext<IndexingSubscription>();

	private readonly IIndexingComponent _component;
	private readonly IIndexingEventSourceFactory _eventSourceFactory;
	private readonly IndexingSubscriptionOptions _options;
	private readonly CancellationTokenSource _stop = new();
	private readonly object _stateLock = new();

	private IIndexingProcessor _processor;
	private IIndexingEventSource _eventSource;
	private IndexCheckpointCommitTracker _commitTracker;
	private Task _startup;
	private Task _processing;
	private bool _starting;
	private bool _started;
	private bool _disposed;

	public IndexingSubscription(
		IIndexingComponent component,
		IIndexingEventSourceFactory eventSourceFactory,
		IndexingSubscriptionOptions options)
	{
		_component = component ?? throw new ArgumentNullException(nameof(component));
		_eventSourceFactory = eventSourceFactory ?? throw new ArgumentNullException(nameof(eventSourceFactory));
		_options = options ?? throw new ArgumentNullException(nameof(options));
	}

	public ValueTask Start(CancellationToken token)
	{
		Task startup;
		lock (_stateLock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_started || _starting)
			{
				throw new InvalidOperationException($"{nameof(IndexingSubscription)} has already been started.");
			}

			_starting = true;
			startup = StartCore(token);
			_startup = startup;
		}

		return new ValueTask(startup);
	}

	private async Task StartCore(CancellationToken token)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);

		IIndexingEventSource eventSource = null;
		IndexCheckpointCommitTracker commitTracker = null;

		try
		{
			token.ThrowIfCancellationRequested();
			await _component.Initialize(linked.Token);
			var checkpoint = await _component.ReadCheckpoint(linked.Token);
			token.ThrowIfCancellationRequested();

			var processor = _component.Processor
				?? throw new InvalidOperationException("Indexing component returned null processor.");

			commitTracker = new IndexCheckpointCommitTracker(
				_options.CheckpointCommitBatchSize,
				_options.CheckpointCommitDelay,
				processor.Commit,
				CancellationToken.None);

			eventSource = _eventSourceFactory.Create(checkpoint, _stop.Token)
				?? throw new InvalidOperationException("Indexing event source factory returned null.");
			token.ThrowIfCancellationRequested();

			lock (_stateLock)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);

				_commitTracker = commitTracker;
				_processor = processor;
				_eventSource = eventSource;
				_processing = Task.Run(ProcessEvents);
				ObserveProcessingFault(_processing);
				_started = true;
				_starting = false;
			}
		}
		catch
		{
			lock (_stateLock)
			{
				_starting = false;
			}

			if (commitTracker is not null)
			{
				await commitTracker.DisposeAsync();
			}

			if (eventSource is not null)
			{
				await eventSource.DisposeAsync();
			}

			throw;
		}
	}

	public ValueTask DisposeAsync() => DisposeAsync(ignoreProcessingFailure: false);

	private async ValueTask DisposeAsync(bool ignoreProcessingFailure)
	{
		Task startup;
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
			startup = _startup;
		}

		await _stop.CancelAsync();

		Exception failure = null;

		try
		{
			if (startup is not null)
			{
				await startup;
			}
		}
		catch (OperationCanceledException) when (_stop.IsCancellationRequested)
		{
		}
		catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
		{
		}
		catch
		{
		}

		lock (_stateLock)
		{
			processing = _processing;
			eventSource = _eventSource;
			commitTracker = _commitTracker;
		}

		try
		{
			if (processing is not null)
			{
				try
				{
					await processing;
				}
				catch (OperationCanceledException) when (_stop.IsCancellationRequested)
				{
				}
			}
		}
		catch (Exception) when (ignoreProcessingFailure)
		{
		}
		catch (Exception ex)
		{
			failure = ex;
		}

		try
		{
			if (commitTracker is not null)
			{
				await commitTracker.DisposeAsync();
			}
		}
		catch (Exception ex) when (failure is null)
		{
			failure = ex;
		}

		try
		{
			if (eventSource is not null)
			{
				await eventSource.DisposeAsync();
			}
		}
		catch (Exception ex) when (failure is null)
		{
			failure = ex;
		}

		try
		{
			await _component.DisposeAsync();
		}
		catch (Exception ex) when (failure is null)
		{
			failure = ex;
		}

		try
		{
			_stop.Dispose();
		}
		catch (Exception ex) when (failure is null)
		{
			failure = ex;
		}

		if (failure is not null)
		{
			ExceptionDispatchInfo.Capture(failure).Throw();
		}
	}

	private void ObserveProcessingFault(Task processing)
	{
		processing.ContinueWith(
			task => _ = DisposeAfterProcessingFault(task),
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private async Task DisposeAfterProcessingFault(Task processing)
	{
		Log.Error(processing.Exception, "Indexing subscription stopped unexpectedly");

		try
		{
			await DisposeAsync(ignoreProcessingFailure: true);
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error while disposing failed indexing subscription");
		}
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
				throw new InvalidOperationException("Indexing event source completed unexpectedly.");
			}

			if (_eventSource.Current is not ReadResponse.EventReceived eventReceived)
			{
				continue;
			}

			try
			{
				await _processor.Index(eventReceived.Event, CancellationToken.None);
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
