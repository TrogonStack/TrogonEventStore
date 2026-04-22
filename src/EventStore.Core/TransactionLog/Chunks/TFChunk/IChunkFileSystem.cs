using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.FileNamingStrategy;

namespace EventStore.Core.TransactionLog.Chunks.TFChunk;

public enum ReadOptimizationHint
{
	None,
	RandomAccess,
	SequentialScan
}

public interface IChunkFileSystem
{
	IVersionedFileNamingStrategy NamingStrategy { get; }

	ValueTask<IChunkHandle> OpenForReadAsync(string fileName, ReadOptimizationHint readOptimizationHint, bool asyncIO,
		CancellationToken token);

	ValueTask<ChunkHeader> ReadHeaderAsync(string fileName, CancellationToken token);

	ValueTask<ChunkFooter> ReadFooterAsync(string fileName, CancellationToken token);

	IChunkEnumerator CreateChunkEnumerator();
}
