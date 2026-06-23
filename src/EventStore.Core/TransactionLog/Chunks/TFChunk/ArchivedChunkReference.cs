using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.TransactionLog.Chunks.TFChunk;

internal readonly record struct ArchivedChunkReference(int ChunkStartNumber, int ChunkEndNumber, int ChunkSize)
{
	public long ChunkStartPosition => (long)ChunkStartNumber * ChunkSize;

	public long ChunkEndPosition => (long)(ChunkEndNumber + 1) * ChunkSize;
}

internal interface IArchivedChunkFileSystem
{
	ValueTask<ArchivedChunkReference?> TryGetArchivedChunk(string locator, CancellationToken token);
}
