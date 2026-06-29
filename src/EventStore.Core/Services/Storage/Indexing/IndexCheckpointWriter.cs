using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed class IndexCheckpointWriter
{
	private readonly IIndexCheckpointStore _store;
	private readonly object _lock = new();
	private IndexCheckpoint? _latestCheckpoint;
	private IndexCheckpoint? _pendingCheckpoint;

	public IndexCheckpointWriter(IIndexCheckpointStore store)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
	}

	public async ValueTask<IndexCheckpoint?> Read(CancellationToken token)
	{
		var checkpoint = await _store.Read(token);
		if (checkpoint is not { } latest)
		{
			return checkpoint;
		}

		lock (_lock)
		{
			if (IsAheadOfLatest(latest))
			{
				_latestCheckpoint = latest;
			}
		}

		return checkpoint;
	}

	public void Track(ResolvedEvent resolvedEvent)
	{
		if (!resolvedEvent.OriginalPosition.HasValue)
		{
			throw new InvalidOperationException(
				"Cannot track index checkpoint progress for an event without an original position.");
		}

		var position = resolvedEvent.OriginalPosition.Value;
		var checkpoint = new IndexCheckpoint(position.CommitPosition, position.PreparePosition);

		lock (_lock)
		{
			if (_latestCheckpoint is { } latest)
			{
				var latestPosition = latest.ToPosition();
				var checkpointPosition = checkpoint.ToPosition();

				if (checkpointPosition < latestPosition)
				{
					throw new InvalidOperationException(
						$"Cannot track index checkpoint progress backwards from {latestPosition} to {checkpointPosition}.");
				}

				if (checkpointPosition == latestPosition)
				{
					return;
				}
			}

			_latestCheckpoint = checkpoint;
			_pendingCheckpoint = checkpoint;
		}
	}

	public async ValueTask Commit(CancellationToken token)
	{
		IndexCheckpoint checkpoint;

		lock (_lock)
		{
			if (_pendingCheckpoint is not { } pending)
			{
				return;
			}

			checkpoint = pending;
		}

		await _store.Write(checkpoint, token);

		lock (_lock)
		{
			if (IsAheadOfLatest(checkpoint))
			{
				_latestCheckpoint = checkpoint;
			}

			if (_pendingCheckpoint == checkpoint)
			{
				_pendingCheckpoint = null;
			}
		}
	}

	private bool IsAheadOfLatest(IndexCheckpoint checkpoint) =>
		_latestCheckpoint is not { } latest || checkpoint.ToPosition() > latest.ToPosition();
}
