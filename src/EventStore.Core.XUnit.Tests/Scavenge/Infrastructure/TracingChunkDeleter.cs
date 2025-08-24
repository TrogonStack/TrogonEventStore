using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Scavenging;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class TracingChunkDeleter<TStreamId, TRecord>(IChunkDeleter<TStreamId, TRecord> wrapped, Tracer tracer) :
	IChunkDeleter<TStreamId, TRecord>
{

	private readonly IChunkDeleter<TStreamId, TRecord> _wrapped = wrapped;

	public async ValueTask<bool> DeleteIfNotRetained(
		ScavengePoint scavengePoint,
		IScavengeStateForChunkExecutorWorker<TStreamId> concurrentState,
		IChunkReaderForExecutor<TStreamId, TRecord> physicalChunk,
		CancellationToken ct)
	{

		var deleted = await _wrapped.DeleteIfNotRetained(scavengePoint, concurrentState, physicalChunk, ct);
		var decision = deleted ? "Deleted" : "Retained";
		tracer.Trace($"{decision} Chunk {physicalChunk.ChunkStartNumber}-{physicalChunk.ChunkEndNumber}");
		return deleted;
	}
}
