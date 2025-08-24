using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.Services.Archive.Storage;

public interface IArchiveStorageReader
{
	public ValueTask<long> GetCheckpoint(CancellationToken ct);
	public ValueTask<Stream> GetChunk(string chunkFile, long start, long end, CancellationToken ct);
	public ValueTask<Stream> GetChunk(string chunkFile, CancellationToken ct);
	public IAsyncEnumerable<string> ListChunks(CancellationToken ct);
}
