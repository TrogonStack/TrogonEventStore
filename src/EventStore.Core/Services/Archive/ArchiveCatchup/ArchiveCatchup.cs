using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.Services.Archive.Storage.Exceptions;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.Transforms;
using EventStore.Plugins.Transforms;
using Serilog;

namespace EventStore.Core.Services.Archive.ArchiveCatchup;

// The archive catchup process downloads chunks that are missing locally from the archive.
//
// This is needed in some cases:
// i)  a follower may be far behind the leader and the latter may have already deleted archived chunks locally.
// ii) under normal circumstances, a leader should never need to catch up from the archive. however, if a cluster's
//     data was restored from a backup, we can end up in a situation where the leader-to-be is behind the archive.
//     in this case, we still want all nodes to catch up with the archive *before* joining the cluster to maintain
//     consistency between the data that's in the cluster and in the archive.

public class ArchiveCatchup : IClusterVNodeStartupTask
{
	private readonly string _dbPath;
	private readonly ICheckpoint _writerCheckpoint;
	private readonly ICheckpoint _chaserCheckpoint;
	private readonly ICheckpoint _epochCheckpoint;
	private readonly int _chunkSize;
	private readonly IVersionedFileNamingStrategy _fileNamingStrategy;
	private readonly IArchiveStorageReader _archiveReader;
	private readonly IArchiveMetrics _metrics;
	private readonly Func<TransformType, IChunkTransformFactory> _getTransformFactory;
	private readonly TimeSpan _retryInterval;
	private readonly Action<string> _deleteTempFile;

	private static readonly ILogger Log = Serilog.Log.ForContext<ArchiveCatchup>();
	private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMinutes(1);

	public ArchiveCatchup(
		string dbPath,
		ICheckpoint writerCheckpoint,
		ICheckpoint chaserCheckpoint,
		ICheckpoint epochCheckpoint,
		int chunkSize,
		IVersionedFileNamingStrategy fileNamingStrategy,
		IArchiveStorageFactory archiveStorageFactory,
		IArchiveMetrics metrics = null,
		Func<TransformType, IChunkTransformFactory> getTransformFactory = null,
		TimeSpan? retryInterval = null,
		Action<string> deleteTempFile = null)
	{
		_dbPath = dbPath;
		_writerCheckpoint = writerCheckpoint;
		_chaserCheckpoint = chaserCheckpoint;
		_epochCheckpoint = epochCheckpoint;
		_chunkSize = chunkSize;
		_fileNamingStrategy = fileNamingStrategy;
		_archiveReader = archiveStorageFactory.CreateReader();
		_metrics = metrics ?? IArchiveMetrics.NoOp;
		_getTransformFactory = getTransformFactory ?? DbTransformManager.Default.GetFactoryForExistingChunk;
		_retryInterval = retryInterval ?? DefaultRetryInterval;
		_deleteTempFile = deleteTempFile ?? File.Delete;
	}

	public async Task Run(CancellationToken ct = default)
	{
		var writerChk = _writerCheckpoint.Read();
		var archiveChk = await GetArchiveCheckpoint(ct);

		if (writerChk >= archiveChk)
		{
			return;
		}

		Log.Information(
			"Catching up with the archive. Writer checkpoint: 0x{writerCheckpoint:X}, Archive checkpoint: 0x{archiveCheckpoint:X}.",
			writerChk, archiveChk);

		while (!await CatchUpWithArchive(writerChk, ct))
		{
			writerChk = _writerCheckpoint.Read();
		}
	}

	// returns true if the catchup is done
	// returns false if it needs to be invoked again to continue the catchup
	private async Task<bool> CatchUpWithArchive(long writerChk, CancellationToken ct)
	{
		string previousChunk = null;
		var firstChunksToFetch = new List<string>(capacity: 2);

		await using var enumerator = _archiveReader.ListChunks(ct).GetAsyncEnumerator(ct);

		// after this loop, the enumerator will be positioned just after the first chunk in the archive that starts
		// at or after the writer checkpoint
		while (await enumerator.MoveNextAsync())
		{
			var chunk = enumerator.Current;
			var chunkStartPos = CalcChunkStartPosition(chunk);

			if (chunkStartPos == writerChk)
			{
				firstChunksToFetch.Add(chunk);
				break;
			}

			if (chunkStartPos > writerChk)
			{
				firstChunksToFetch.Add(previousChunk);
				firstChunksToFetch.Add(chunk);
				break;
			}

			previousChunk = chunk;
		}

		// we have gone through all the chunks in the archive but could not find one that starts at or after the writer
		// checkpoint. this case can happen when the database is (less than) one chunk behind the archive.
		// we already know that we are behind the archive as we've compared the checkpoints at the beginning, so there
		// must be at least one chunk to fetch from the archive: the last chunk.
		if (firstChunksToFetch.Count == 0)
		{
			if (previousChunk == null)
			{
				// `previousChunk` cannot be null, there must be at least one chunk in the archive
				// (we would not be here otherwise: we cannot be behind the archive if it is empty)
				throw new Exception("There are no chunks in the archive");
			}

			firstChunksToFetch.Add(previousChunk);
		}

		// fetch the first one or two chunks
		foreach (var chunk in firstChunksToFetch)
		{
			if (!await FetchAndCommitChunk(chunk, ct))
			{
				return false;
			}
		}

		// all the remaining chunks are definitely after the writer checkpoint
		while (await enumerator.MoveNextAsync())
		{
			if (!await FetchAndCommitChunk(enumerator.Current, ct))
			{
				return false;
			}
		}

		Log.Information("Catch-up with the archive completed");
		return true;
	}

	private async Task<long> GetArchiveCheckpoint(CancellationToken ct)
	{
		do
		{
			try
			{
				return await _archiveReader.GetCheckpoint(ct);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_metrics.RecordFailure(ArchiveOperation.CatchUpCheckpoint);
				_metrics.RecordRetry(ArchiveOperation.CatchUpCheckpoint);
				Log.Error(ex, "Failed to get archive checkpoint. Retrying in: {interval}", _retryInterval);
				await Task.Delay(_retryInterval, ct);
			}
		} while (true);
	}

	private long CalcChunkStartPosition(string chunk)
	{
		var chunkNumber = _fileNamingStrategy.GetIndexFor(chunk);
		return (long)chunkNumber * _chunkSize;
	}

	private async Task<bool> FetchAndCommitChunk(string chunkFile, CancellationToken ct)
	{
		var chunkPath = Path.Combine(_dbPath, chunkFile);

		if (!await FetchChunk(chunkFile, chunkPath, ct))
		{
			return false;
		}

		await CommitChunk(chunkPath, ct);
		return true;
	}

	private async Task<bool> FetchChunk(string chunkFile, string destinationPath, CancellationToken ct)
	{
		string tempPath = null;
		try
		{
			Log.Information("Fetching {chunk} from the archive", chunkFile);

			tempPath = Path.Combine(_dbPath, Guid.NewGuid() + ".archive.tmp");

			await using (var inputStream = await _archiveReader.GetChunk(chunkFile, ct))
			{
				await using var outputStream = File.Open(
					path: tempPath,
					options: new FileStreamOptions
					{
						Mode = FileMode.CreateNew,
						Access = FileAccess.ReadWrite,
						Share = FileShare.None,
						Options = FileOptions.Asynchronous,
						PreallocationSize = _chunkSize
					});

				await inputStream.CopyToAsync(outputStream, ct);
				outputStream.SetLength(outputStream.Position);
			}

			using (await TFChunk.FromCompletedFile(
				new ChunkLocalFileSystem(_fileNamingStrategy),
				tempPath,
				verifyHash: true,
				unbufferedRead: false,
				tracker: new TFChunkTracker.NoOp(),
				getTransformFactory: _getTransformFactory,
				token: ct))
			{ }

			if (File.Exists(destinationPath))
			{
				var backupPath = $"{destinationPath}.archive.bkup";
				Log.Information("Backing up {chunk} to {chunkBackup}", Path.GetFileName(destinationPath),
					Path.GetFileName(backupPath));
				File.Move(destinationPath, backupPath, overwrite: true);
			}

			File.Move(tempPath, destinationPath);
			tempPath = null;

			return true;
		}
		catch (ChunkDeletedException)
		{
			DeleteTempFile(ref tempPath);
			_metrics.RecordFailure(ArchiveOperation.CatchUpChunk);
			_metrics.RecordRetry(ArchiveOperation.CatchUpChunk);
			Log.Warning(
				"Failed to fetch {chunk} from the archive as it was deleted. This can happen if the archive is being scavenged. Retrying in {interval}.",
				chunkFile, _retryInterval);
			await Task.Delay(_retryInterval, ct);
			return false;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			DeleteTempFile(ref tempPath);
			_metrics.RecordFailure(ArchiveOperation.CatchUpChunk);
			_metrics.RecordRetry(ArchiveOperation.CatchUpChunk);
			Log.Error(ex, "Failed to fetch {chunk} from the archive. Retrying in {interval}", chunkFile, _retryInterval);
			await Task.Delay(_retryInterval, ct);
			return false;
		}
		finally
		{
			DeleteTempFile(ref tempPath);
		}
	}

	private void DeleteTempFile(ref string tempPath)
	{
		var path = tempPath;
		tempPath = null;
		if (path is null)
		{
			return;
		}

		try
		{
			_deleteTempFile(path);
		}
		catch (Exception ex)
		{
			Log.Warning(ex, "Failed to delete temporary archive file {tempFile}", Path.GetFileName(path));
		}
	}

	private async Task CommitChunk(string chunkPath, CancellationToken ct)
	{
		await using var headerStream = File.OpenRead(chunkPath);
		var header = await ChunkHeader.FromStream(headerStream, ct);

		_epochCheckpoint.Write(-1);
		_epochCheckpoint.Flush();
		Log.Debug("Reset {checkpoint} checkpoint to: 0x{position:X}", _epochCheckpoint.Name, -1);

		_chaserCheckpoint.Write(header.ChunkEndPosition);
		_chaserCheckpoint.Flush();
		Log.Debug("Moved {checkpoint} checkpoint forward to: 0x{position:X}", _chaserCheckpoint.Name,
			header.ChunkEndPosition);

		_writerCheckpoint.Write(header.ChunkEndPosition);
		_writerCheckpoint.Flush();
		Log.Debug("Moved {checkpoint} checkpoint forward to: 0x{position:X}", _writerCheckpoint.Name,
			header.ChunkEndPosition);
	}
}
