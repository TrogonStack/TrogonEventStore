using System;
using System.Collections.Generic;
using System.Threading;
using EventStore.Core.Services.Archive;

namespace EventStore.Core.XUnit.Tests.Services.Archive;

internal sealed class RecordingArchiveMetrics : IArchiveMetrics
{
	private long _replicationPosition;
	private long _checkpoint;
	private int _maxUncommittedChunks;
	private int _maxQueuedChunks;
	private int _maxActiveChunks;

	public List<ArchiveOperation> Failures { get; } = [];
	public List<ArchiveOperation> Retries { get; } = [];
	public List<(ArchiveOperation Operation, TimeSpan Duration, bool Succeeded)> Reads { get; } = [];
	public long ReplicationPosition => Interlocked.Read(ref _replicationPosition);
	public long Checkpoint => Interlocked.Read(ref _checkpoint);
	public int MaxUncommittedChunks => Volatile.Read(ref _maxUncommittedChunks);
	public int MaxQueuedChunks => Volatile.Read(ref _maxQueuedChunks);
	public int MaxActiveChunks => Volatile.Read(ref _maxActiveChunks);

	public void SetReplicationPosition(long position) => Interlocked.Exchange(ref _replicationPosition, position);
	public void SetCheckpoint(long position) => Interlocked.Exchange(ref _checkpoint, position);
	public void SetUncommittedChunks(int count) => UpdateMax(ref _maxUncommittedChunks, count);
	public void SetQueuedChunks(int count) => UpdateMax(ref _maxQueuedChunks, count);
	public void SetActiveChunks(int count) => UpdateMax(ref _maxActiveChunks, count);
	public void RecordRetry(ArchiveOperation operation) => Retries.Add(operation);
	public void RecordFailure(ArchiveOperation operation) => Failures.Add(operation);
	public void RecordRead(ArchiveOperation operation, TimeSpan duration, bool succeeded) =>
		Reads.Add((operation, duration, succeeded));

	private static void UpdateMax(ref int target, int value)
	{
		var current = Volatile.Read(ref target);
		while (value > current)
		{
			var observed = Interlocked.CompareExchange(ref target, value, current);
			if (observed == current)
			{
				return;
			}

			current = observed;
		}
	}
}
