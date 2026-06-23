namespace EventStore.Core.TransactionLog.Chunks.TFChunk;

internal readonly record struct ArchivedChunkReference(int ChunkNumber, int ChunkSize)
{
	public int ChunkEndNumber => ChunkNumber;

	public long ChunkStartPosition => (long)ChunkNumber * ChunkSize;

	public long ChunkEndPosition => ChunkStartPosition + ChunkSize;
}

internal interface IArchivedChunkFileSystem
{
	bool TryGetArchivedChunk(string locator, out ArchivedChunkReference archivedChunk);
}
