using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.TransactionLog.Scavenging;

public interface IChunkDeleter<TStreamId, TRecord> {
	static IChunkDeleter<TStreamId, TRecord> NoOp => NoOpChunkDeleter<TStreamId, TRecord>.Instance;

	// returns true if deleted
	ValueTask<bool> DeleteIfNotRetained(
		ScavengePoint scavengePoint,
		IScavengeStateForChunkExecutorWorker<TStreamId> concurrentState,
		IChunkReaderForExecutor<TStreamId, TRecord> physicalChunk,
		CancellationToken ct);
}

file class NoOpChunkDeleter<TStreamId, TRecord> : IChunkDeleter<TStreamId, TRecord>
{
	public static NoOpChunkDeleter<TStreamId, TRecord> Instance { get; } = new();

	public ValueTask<bool> DeleteIfNotRetained(
		ScavengePoint scavengePoint,
		IScavengeStateForChunkExecutorWorker<TStreamId> concurrentState,
		IChunkReaderForExecutor<TStreamId, TRecord> physicalChunk, CancellationToken ct)
	{

		return new(false);
	}
}
