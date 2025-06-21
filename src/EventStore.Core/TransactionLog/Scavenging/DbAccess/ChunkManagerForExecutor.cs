using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Core.Transforms;
using Serilog;

namespace EventStore.Core.TransactionLog.Scavenging;

public class ChunkManagerForExecutor<TStreamId>(
	ILogger logger,
	TFChunkManager manager,
	TFChunkDbConfig dbConfig,
	DbTransformManager transformManager)
	: IChunkManagerForChunkExecutor<TStreamId, ILogRecord>
{
	public async ValueTask<IChunkWriterForExecutor<TStreamId, ILogRecord>> CreateChunkWriter(
		IChunkReaderForExecutor<TStreamId, ILogRecord> sourceChunk,
		CancellationToken token)
		=> await ChunkWriterForExecutor<TStreamId>.CreateAsync(logger, this, dbConfig, sourceChunk,
			transformManager, token);

	public IChunkReaderForExecutor<TStreamId, ILogRecord> GetChunkReaderFor(long position)
	{
		var tfChunk = manager.GetChunkFor(position);
		return new ChunkReaderForExecutor<TStreamId>(tfChunk);
	}

	public async ValueTask<string> SwitchChunk(
		TFChunk chunk,
		CancellationToken token)
	{

		var tfChunk = await manager.SwitchChunk(
			chunk: chunk,
			verifyHash: false,
			removeChunksWithGreaterNumbers: false,
			token);

		if (tfChunk is null)
		{
			throw new Exception("Unexpected error: new chunk is null after switch");
		}

		return tfChunk.FileName;
	}
}
