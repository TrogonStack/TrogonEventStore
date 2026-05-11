using System;
using System.Collections.Generic;

namespace EventStore.Core.Services.Archive.Archiver.Unmerger;

public class ChunkUnmerger : IChunkUnmerger
{
	public IAsyncEnumerable<string> Unmerge(string chunkPath, int chunkStartNumber, int chunkEndNumber)
	{
		throw new NotImplementedException();
	}
}
