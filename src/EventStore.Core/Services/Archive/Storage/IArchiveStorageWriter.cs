using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive.Storage.Exceptions;

namespace EventStore.Core.Services.Archive.Storage;

public interface IArchiveStorageWriter
{
	public ValueTask<bool> SetCheckpoint(long checkpoint, CancellationToken ct);
	public ValueTask<bool> StoreChunk(string chunkPath, string destinationFile, CancellationToken ct);
}
