using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Core.Transforms;
using Serilog;

namespace EventStore.Core.TransactionLog.Scavenging;

public class ChunkWriterForExecutor<TStreamId> : IChunkWriterForExecutor<TStreamId, ILogRecord>
{
	const int BatchLength = 2000;
	private readonly ILogger _logger;
	private readonly ChunkManagerForExecutor<TStreamId> _manager;
	private readonly TFChunk _outputChunk;
	private readonly List<List<PosMap>> _posMapss;
	private int _lastFlushedPage = -1;

	private ChunkWriterForExecutor(
		ILogger logger,
		ChunkManagerForExecutor<TStreamId> manager,
		TFChunk outputChunk)
	{

		_logger = logger;
		_manager = manager;

		// list of lists to avoid having an enormous list which could make it to the LoH
		// and to avoid expensive resize operations on large lists
		_posMapss = new List<List<PosMap>>(capacity: BatchLength) { new(capacity: BatchLength) };

		_outputChunk = outputChunk;
	}

	public static async ValueTask<ChunkWriterForExecutor<TStreamId>> CreateAsync(
		ILogger logger,
		ChunkManagerForExecutor<TStreamId> manager,
		TFChunkDbConfig dbConfig,
		IChunkReaderForExecutor<TStreamId, ILogRecord> sourceChunk,
		DbTransformManager transformManager,
		CancellationToken token)
	{

		// from TFChunkScavenger.ScavengeChunk
		var chunk = await TFChunk.CreateNew(
			filename: Path.Combine(dbConfig.Path, Guid.NewGuid() + ".scavenge.tmp"),
			chunkDataSize: dbConfig.ChunkSize,
			chunkStartNumber: sourceChunk.ChunkStartNumber,
			chunkEndNumber: sourceChunk.ChunkEndNumber,
			isScavenged: true,
			inMem: dbConfig.InMemDb,
			unbuffered: dbConfig.Unbuffered,
			writethrough: dbConfig.WriteThrough,
			reduceFileCachePressure: dbConfig.ReduceFileCachePressure,
			tracker: new TFChunkTracker.NoOp(),
			transformFactory: transformManager.GetFactoryForNewChunk(),
			token);

		return new(logger, manager, chunk);
	}

	public string FileName => _outputChunk.FileName;

	public void WriteRecord(RecordForExecutor<TStreamId, ILogRecord> record)
	{
		var posMap = TFChunkScavenger<TStreamId>.WriteRecord(_outputChunk, record.Record);

		// add the posmap in memory so we can write it when we complete
		var lastBatch = _posMapss[_posMapss.Count - 1];
		if (lastBatch.Count >= BatchLength)
		{
			lastBatch = new List<PosMap>(capacity: BatchLength);
			_posMapss.Add(lastBatch);
		}

		lastBatch.Add(posMap);

		// occasionally flush the chunk. based on TFChunkScavenger.ScavengeChunk
		var currentPage = _outputChunk.RawWriterPosition / 4046;
		if (currentPage - _lastFlushedPage > TFChunkScavenger.FlushPageInterval)
		{
			_outputChunk.Flush();
			_lastFlushedPage = currentPage;
		}
	}

	public async ValueTask<(string, long)> Complete(CancellationToken token)
	{
		// write posmap
		var posMapCount = 0;
		foreach (var list in _posMapss)
			posMapCount += list.Count;

		var unifiedPosMap = new List<PosMap>(capacity: posMapCount);
		foreach (var list in _posMapss)
			unifiedPosMap.AddRange(list);

		await _outputChunk.CompleteScavenge(unifiedPosMap, token);
		var newFileName = await _manager.SwitchChunk(chunk: _outputChunk, token);

		return (newFileName, _outputChunk.FileSize);
	}

	// tbh not sure why this distinction is important
	public void Abort(bool deleteImmediately)
	{
		if (deleteImmediately)
		{
			_outputChunk.Dispose();
			TFChunkScavenger<TStreamId>.DeleteTempChunk(_logger, FileName, TFChunkScavenger.MaxRetryCount);
		}
		else
		{
			_outputChunk.MarkForDeletion();
		}
	}
}
