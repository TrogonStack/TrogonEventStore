using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.Transforms;
using EventStore.Plugins.Transforms;

namespace EventStore.Core.Services.Archive.Archiver.Unmerger;

public sealed class ChunkUnmerger : IChunkUnmerger
{
	private readonly TFChunkDbConfig _dbConfig;
	private readonly DbTransformManager _transformManager;

	public ChunkUnmerger(StandardComponents standardComponents, IReadOnlyList<IDbTransform> transforms)
		: this(standardComponents.DbConfig, CreateTransformManager(transforms))
	{
	}

	public ChunkUnmerger(TFChunkDbConfig dbConfig, DbTransformManager transformManager)
	{
		_dbConfig = dbConfig ?? throw new ArgumentNullException(nameof(dbConfig));
		_transformManager = transformManager ?? throw new ArgumentNullException(nameof(transformManager));
	}

	public IAsyncEnumerable<string> Unmerge(string chunkPath, int chunkStartNumber, int chunkEndNumber) =>
		UnmergeCore(chunkPath, chunkStartNumber, chunkEndNumber);

	private async IAsyncEnumerable<string> UnmergeCore(
		string chunkPath,
		int chunkStartNumber,
		int chunkEndNumber,
		[EnumeratorCancellation] CancellationToken token = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(chunkPath);
		if (chunkEndNumber < chunkStartNumber)
		{
			throw new ArgumentOutOfRangeException(nameof(chunkEndNumber), chunkEndNumber,
				"Chunk end number must be greater than or equal to chunk start number.");
		}

		var tempFiles = new List<string>(chunkEndNumber - chunkStartNumber + 1);
		var ownershipTransferred = false;

		try
		{
			using var sourceChunk = await TFChunk.FromCompletedFile(
				_dbConfig.ChunkFileSystem,
				chunkPath,
				verifyHash: false,
				unbufferedRead: _dbConfig.Unbuffered,
				tracker: new TFChunkTracker.NoOp(),
				getTransformFactory: _transformManager.GetFactoryForExistingChunk,
				reduceFileCachePressure: _dbConfig.ReduceFileCachePressure,
				asyncIO: _dbConfig.AsyncIO,
				token);

			if (sourceChunk.ChunkHeader.ChunkStartNumber != chunkStartNumber ||
				sourceChunk.ChunkHeader.ChunkEndNumber != chunkEndNumber)
			{
				throw new InvalidOperationException(
					$"Chunk '{Path.GetFileName(chunkPath)}' range #{sourceChunk.ChunkHeader.ChunkStartNumber}-{sourceChunk.ChunkHeader.ChunkEndNumber} does not match requested range #{chunkStartNumber}-{chunkEndNumber}.");
			}

			var transformFactory = _transformManager.GetFactoryForExistingChunk(sourceChunk.ChunkHeader.TransformType);
			for (var chunkNumber = chunkStartNumber; chunkNumber <= chunkEndNumber; chunkNumber++)
			{
				var tempFile = _dbConfig.FileNamingStrategy.CreateTempFilename();
				await WriteLogicalChunk(sourceChunk, tempFile, chunkNumber, transformFactory, token);
				tempFiles.Add(tempFile);
			}

			foreach (var tempFile in tempFiles)
			{
				token.ThrowIfCancellationRequested();
				yield return tempFile;
			}

			ownershipTransferred = true;
		}
		finally
		{
			if (!ownershipTransferred)
			{
				foreach (var tempFile in tempFiles)
				{
					DeleteTempFile(tempFile);
				}
			}
		}
	}

	private async ValueTask WriteLogicalChunk(
		TFChunk sourceChunk,
		string tempFile,
		int chunkNumber,
		IChunkTransformFactory transformFactory,
		CancellationToken token)
	{
		TFChunk targetChunk = null;
		try
		{
			targetChunk = await TFChunk.CreateNew(
				_dbConfig.ChunkFileSystem,
				tempFile,
				_dbConfig.ChunkSize,
				chunkNumber,
				chunkNumber,
				isScavenged: true,
				unbuffered: _dbConfig.Unbuffered,
				writethrough: _dbConfig.WriteThrough,
				reduceFileCachePressure: _dbConfig.ReduceFileCachePressure,
				asyncIO: _dbConfig.AsyncIO,
				tracker: new TFChunkTracker.NoOp(),
				transformFactory,
				token);

			var positionMap = await CopyRecords(sourceChunk, targetChunk, chunkNumber, token);
			await targetChunk.CompleteScavenge(positionMap, token);
		}
		catch
		{
			targetChunk?.MarkForDeletion();
			DeleteTempFile(tempFile);
			throw;
		}
		finally
		{
			targetChunk?.Dispose();
		}
	}

	private async ValueTask<List<PosMap>> CopyRecords(
		TFChunk sourceChunk,
		TFChunk targetChunk,
		int chunkNumber,
		CancellationToken token)
	{
		var positionMap = new List<PosMap>();
		var chunkStartPosition = chunkNumber * (long)_dbConfig.ChunkSize;
		var chunkEndPosition = (chunkNumber + 1) * (long)_dbConfig.ChunkSize;
		var sourcePosition = sourceChunk.ChunkHeader.GetLocalLogPosition(chunkStartPosition);

		while (true)
		{
			var result = await sourceChunk.TryReadClosestForward(sourcePosition, token);
			if (!result.Success || result.LogRecord.LogPosition >= chunkEndPosition)
			{
				return positionMap;
			}

			if (result.LogRecord.LogPosition >= chunkStartPosition)
			{
				var writeResult = await targetChunk.TryAppend(result.LogRecord, token);
				if (!writeResult.Success)
				{
					throw new InvalidOperationException(
						$"Unable to append record at 0x{result.LogRecord.LogPosition:X} while unmerging chunk #{chunkNumber}.");
				}

				positionMap.Add(new PosMap(
					targetChunk.ChunkHeader.GetLocalLogPosition(result.LogRecord.LogPosition),
					(int)writeResult.OldPosition));
			}

			sourcePosition = result.NextPosition;
		}
	}

	private void DeleteTempFile(string tempFile)
	{
		try
		{
			_dbConfig.ChunkFileSystem.DeleteFile(tempFile);
		}
		catch
		{
		}
	}

	private static DbTransformManager CreateTransformManager(IReadOnlyList<IDbTransform> transforms)
	{
		ArgumentNullException.ThrowIfNull(transforms);

		var manager = new DbTransformManager();
		manager.LoadTransforms(transforms);
		return manager;
	}
}
