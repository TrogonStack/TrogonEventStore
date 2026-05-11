using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Scavenging;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class TracingChunkManagerForChunkExecutor<TStreamId, TRecord>(
	IChunkManagerForChunkExecutor<TStreamId, TRecord> wrapped,
	HashSet<int> remoteChunks,
	Tracer tracer)
	:
		IChunkManagerForChunkExecutor<TStreamId, TRecord>
{
	private readonly HashSet<int> _remoteChunks = remoteChunks;

	public async ValueTask<IChunkWriterForExecutor<TStreamId, TRecord>> CreateChunkWriter(
		IChunkReaderForExecutor<TStreamId, TRecord> sourceChunk,
		CancellationToken token)
	{

		return new TracingChunkWriterForExecutor<TStreamId, TRecord>(
			await wrapped.CreateChunkWriter(sourceChunk, token),
			tracer);
	}

	public IChunkReaderForExecutor<TStreamId, TRecord> GetChunkReaderFor(long position)
	{
		var reader = wrapped.GetChunkReaderFor(position);
		var isRemote = _remoteChunks.Contains(reader.ChunkStartNumber);
		return new TrackingChunkReaderForExecutor<TStreamId, TRecord>(reader, isRemote, tracer);
	}
}
