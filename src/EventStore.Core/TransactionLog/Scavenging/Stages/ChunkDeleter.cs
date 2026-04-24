#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using Serilog;

namespace EventStore.Core.TransactionLog.Scavenging.Stages;

public class ChunkDeleter<TStreamId, TRecord> : IChunkDeleter<TStreamId, TRecord>
{
	private readonly ILogger _logger;
	private readonly AdvancingCheckpoint _archiveCheckpoint;
	private readonly IChunkManagerForChunkDeleter _chunkManager;
	private readonly ILocatorCodec _locatorCodec;
	private readonly TimeSpan _retainPeriod;
	private readonly long _retainBytes;
	private readonly int _maxAttempts;
	private readonly TimeSpan _retryDelay;

	public ChunkDeleter(
		ILogger logger,
		AdvancingCheckpoint archiveCheckpoint,
		IChunkManagerForChunkDeleter chunkManager,
		ILocatorCodec locatorCodec,
		TimeSpan retainPeriod,
		long retainBytes,
		int maxAttempts = 10,
		int retryDelayMs = 1000)
	{

		_logger = logger;
		_archiveCheckpoint = archiveCheckpoint;
		_chunkManager = chunkManager;
		_locatorCodec = locatorCodec;
		_retainPeriod = retainPeriod;
		_retainBytes = retainBytes;
		_maxAttempts = maxAttempts;
		_retryDelay = TimeSpan.FromMilliseconds(retryDelayMs);

		_logger.Debug("SCAVENGING: Chunk retention criteria is Days: {Days}, LogicalBytes: {LogicalBytes}",
			retainPeriod.Days,
			retainBytes);
	}

	// returns true iff deleted
	public async ValueTask<bool> DeleteIfNotRetained(
		ScavengePoint scavengePoint,
		IScavengeStateForChunkExecutorWorker<TStreamId> concurrentState,
		IChunkReaderForExecutor<TStreamId, TRecord> physicalChunk,
		CancellationToken ct)
	{
		if (physicalChunk.IsRemote)
			return false;

		if (!ShouldDeleteForBytes(scavengePoint, physicalChunk))
		{
			_logger.Debug(
				"SCAVENGING: Keeping physical chunk {physicalChunk} because it is still within the logical bytes retention window.",
				physicalChunk.Name);
			return false;
		}

		if (!ShouldDeleteForPeriod(scavengePoint, concurrentState, physicalChunk))
		{
			_logger.Debug(
				"SCAVENGING: Keeping physical chunk {physicalChunk} because it is still within the retention period window.",
				physicalChunk.Name);
			return false;
		}

		if (!await IsConfirmedPresentInArchive(physicalChunk, ct))
			return false;

		return await SwitchOutPhysicalChunk(physicalChunk, ct);
	}

	private async ValueTask<bool> IsConfirmedPresentInArchive(
		IChunkReaderForExecutor<TStreamId, TRecord> physicalChunk,
		CancellationToken ct)
	{
		var logicalChunkNumber = physicalChunk.ChunkEndNumber;

		for (var attempt = 0; attempt < _maxAttempts; attempt++)
		{
			if (attempt != 0)
				await Task.Delay(_retryDelay, ct);

			try
			{
				var isPresent = await _archiveCheckpoint.IsGreaterThanOrEqualTo(physicalChunk.ChunkEndPosition, ct);
				if (isPresent)
					return true;

				if (attempt == _maxAttempts - 1)
				{
					_logger.Warning(
						"Logical chunk {LogicalChunkNumber} is not yet present in the archive.",
						logicalChunkNumber);
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logger.Warning(ex,
					"Unable to determine existence of logical chunk {LogicalChunkNumber} in the archive. Attempt {Attempt}/{MaxAttempts}",
					logicalChunkNumber, attempt + 1, _maxAttempts);
			}
		}

		return false;
	}

	private bool ShouldDeleteForBytes(
		ScavengePoint scavengePoint,
		IChunkReaderForExecutor<TStreamId, TRecord> physicalChunk)
	{

		var deleteBytesBefore = scavengePoint.Position - _retainBytes;
		return physicalChunk.ChunkEndPosition < deleteBytesBefore;
	}

	private bool ShouldDeleteForPeriod(
		ScavengePoint scavengePoint,
		IScavengeStateForChunkExecutorWorker<TStreamId> concurrentState,
		IChunkReaderForExecutor<TStreamId, TRecord> physicalChunk)
	{

		for (var logicalChunkNumber = physicalChunk.ChunkEndNumber;
		     logicalChunkNumber >= physicalChunk.ChunkStartNumber;
		     logicalChunkNumber--)
		{

			if (concurrentState.TryGetChunkTimeStampRange(logicalChunkNumber, out var createdAtRange))
			{
				var deleteBefore = scavengePoint.EffectiveNow - _retainPeriod;
				return createdAtRange.Max < deleteBefore;
			}
			else
			{
				// we don't have a time stamp range for this logical chunk, it had no prepares in during
				// accumulation. we try an earlier logical chunk in this physical chunk.
			}
		}

		// no time stamp for any logical chunk in this physical chunk. its possible to get here if the
		// physical chunk doesn't have any prepares in it at all. it's fine to delete it.
		return true;
	}

	private async ValueTask<bool> SwitchOutPhysicalChunk(
		IChunkReaderForExecutor<TStreamId, TRecord> physicalChunk,
		CancellationToken ct)
	{
		var chunkStartNumber = physicalChunk.ChunkStartNumber;
		var chunkEndNumber = physicalChunk.ChunkEndNumber;
		_logger.Debug(
			"SCAVENGING: Deleting physical chunk: {oldChunkName} " +
			"{chunkStartNumber} => {chunkEndNumber} ({chunkStartPosition} => {chunkEndPosition})",
			physicalChunk.Name,
			chunkStartNumber, chunkEndNumber,
			physicalChunk.ChunkStartPosition, physicalChunk.ChunkEndPosition);

		var locators = new string[chunkEndNumber - chunkStartNumber + 1];
		for (var i = 0; i < locators.Length; i++)
		{
			locators[i] = _locatorCodec.EncodeRemote(chunkStartNumber + i);
		}

		if (await _chunkManager.SwitchInChunks(locators, ct))
			return true;

		_logger.Warning(
			"SCAVENGING: Did not delete physical chunk: {oldChunkName} {chunkStartNumber} => {chunkEndNumber}. This will be retried next scavenge.",
			physicalChunk.Name,
			chunkStartNumber,
			chunkEndNumber);
		return false;
	}
}
