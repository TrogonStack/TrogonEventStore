using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive.Naming;

namespace EventStore.Core.Services.Archive.Storage;

public interface IArchiveStorageReader
{
	public IArchiveChunkNamer ChunkNamer { get; }
	public ValueTask<long> GetCheckpoint(CancellationToken ct);
	public ValueTask<long> GetChunkLength(string chunkFile, CancellationToken ct);
	public ValueTask<Stream> GetChunk(string chunkFile, long start, long end, CancellationToken ct);
	public ValueTask<Stream> GetChunk(string chunkFile, CancellationToken ct);
	public IAsyncEnumerable<string> ListChunks(CancellationToken ct);
}
