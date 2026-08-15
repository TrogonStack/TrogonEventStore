using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using EventStore.Core.Metrics;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.Services.Archive;

public enum ArchiveOperation
{
	StoreChunk,
	SetCheckpoint,
	LoadCheckpoint,
	ReadMetadata,
	ReadFull,
	ReadRange,
	CatchUpCheckpoint,
	CatchUpChunk,
	Service
}

public interface IArchiveMetrics
{
	void SetReplicationPosition(long position);
	void SetCheckpoint(long position);
	void SetUncommittedChunks(int count);
	void SetQueuedChunks(int count);
	void SetActiveChunks(int count);
	void RecordRetry(ArchiveOperation operation);
	void RecordFailure(ArchiveOperation operation);
	void RecordRead(ArchiveOperation operation, TimeSpan duration, bool succeeded);

	public static IArchiveMetrics NoOp { get; } = new NoOpArchiveMetrics();

	private sealed class NoOpArchiveMetrics : IArchiveMetrics
	{
		public void SetReplicationPosition(long position) { }
		public void SetCheckpoint(long position) { }
		public void SetUncommittedChunks(int count) { }
		public void SetQueuedChunks(int count) { }
		public void SetActiveChunks(int count) { }
		public void RecordRetry(ArchiveOperation operation) { }
		public void RecordFailure(ArchiveOperation operation) { }
		public void RecordRead(ArchiveOperation operation, TimeSpan duration, bool succeeded) { }
	}
}

public sealed class ArchiveMetrics : IArchiveMetrics
{
	private readonly Counter<long> _retries;
	private readonly Counter<long> _failures;
	private readonly Histogram<double> _readDuration;
	private long _replicationPosition;
	private long _checkpoint;
	private int _uncommittedChunks;
	private int _queuedChunks;
	private int _activeChunks;

	public ArchiveMetrics(Meter meter, bool observeArchiverState = true)
	{
		ArgumentNullException.ThrowIfNull(meter);

		if (observeArchiverState)
		{
			var checkpointLag = MetricDefinitions.TrogonEventstoreArchiveCheckpointLag;
			checkpointLag.EnsureInstrumentKind(MetricInstrumentKind.Gauge);
			meter.CreateObservableGauge(
				checkpointLag.Name,
				ObserveCheckpointLag,
				checkpointLag.Unit,
				checkpointLag.Description);

			var pendingChunks = MetricDefinitions.TrogonEventstoreArchiveChunkPendingCount;
			pendingChunks.EnsureInstrumentKind(MetricInstrumentKind.UpDownCounter);
			meter.CreateObservableUpDownCounter(
				pendingChunks.Name,
				ObservePendingChunks,
				pendingChunks.Unit,
				pendingChunks.Description);
		}

		_retries = CreateCounter(meter, MetricDefinitions.TrogonEventstoreArchiveRetryCount);
		_failures = CreateCounter(meter, MetricDefinitions.TrogonEventstoreArchiveFailureCount);

		var readDuration = MetricDefinitions.TrogonEventstoreArchiveReadDuration;
		readDuration.EnsureInstrumentKind(MetricInstrumentKind.Histogram);
		_readDuration = meter.CreateHistogram<double>(
			readDuration.Name,
			readDuration.Unit,
			readDuration.Description);
	}

	public void SetReplicationPosition(long position) =>
		Interlocked.Exchange(ref _replicationPosition, position);

	public void SetCheckpoint(long position) =>
		Interlocked.Exchange(ref _checkpoint, position);

	public void SetUncommittedChunks(int count) =>
		Interlocked.Exchange(ref _uncommittedChunks, count);

	public void SetQueuedChunks(int count) =>
		Interlocked.Exchange(ref _queuedChunks, count);

	public void SetActiveChunks(int count) =>
		Interlocked.Exchange(ref _activeChunks, count);

	public void RecordRetry(ArchiveOperation operation) =>
		_retries.Add(1, ActivityName(operation));

	public void RecordFailure(ArchiveOperation operation) =>
		_failures.Add(1, ActivityName(operation));

	public void RecordRead(ArchiveOperation operation, TimeSpan duration, bool succeeded) =>
		_readDuration.Record(
			duration.TotalSeconds,
			ActivityName(operation),
			new KeyValuePair<string, object>(TrogonAttributeNames.ActivityOutcome, succeeded ? "success" : "error"));

	private long ObserveCheckpointLag() =>
		Math.Max(0, Interlocked.Read(ref _replicationPosition) - Interlocked.Read(ref _checkpoint));

	private long ObservePendingChunks() =>
		Volatile.Read(ref _uncommittedChunks) +
		Volatile.Read(ref _queuedChunks) +
		Volatile.Read(ref _activeChunks);

	private static Counter<long> CreateCounter(Meter meter, MetricDefinition definition)
	{
		definition.EnsureInstrumentKind(MetricInstrumentKind.Counter);
		return meter.CreateCounter<long>(definition.Name, definition.Unit, definition.Description);
	}

	private static KeyValuePair<string, object> ActivityName(ArchiveOperation operation) =>
		new(TrogonAttributeNames.ActivityName, operation switch
		{
			ArchiveOperation.StoreChunk => "store-chunk",
			ArchiveOperation.SetCheckpoint => "set-checkpoint",
			ArchiveOperation.LoadCheckpoint => "load-checkpoint",
			ArchiveOperation.ReadMetadata => "read-metadata",
			ArchiveOperation.ReadFull => "read-full",
			ArchiveOperation.ReadRange => "read-range",
			ArchiveOperation.CatchUpCheckpoint => "catch-up-checkpoint",
			ArchiveOperation.CatchUpChunk => "catch-up-chunk",
			ArchiveOperation.Service => "service",
			_ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
		});
}
