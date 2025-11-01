using System.Collections.Generic;

namespace EventStore.Core.Services.Archive.Archiver.Unmerger;

public interface IChunkUnmerger
{
	IAsyncEnumerable<string> Unmerge(string chunkPath, int chunkStartNumber, int chunkEndNumber);
}
