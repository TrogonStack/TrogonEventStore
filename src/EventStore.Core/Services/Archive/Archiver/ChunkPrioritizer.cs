#nullable enable

using System.Collections.Generic;

namespace EventStore.Core.Services.Archive.Archiver;

public class ChunkPrioritizer : IComparer<Commands.ArchiveChunk>
{
	public int Compare(Commands.ArchiveChunk? x, Commands.ArchiveChunk? y)
	{
		int cmp = x!.ChunkStartNumber.CompareTo(y!.ChunkStartNumber);

		if (cmp != 0)
			return cmp;

		cmp = x!.ChunkEndNumber.CompareTo(y!.ChunkEndNumber);
		return cmp;
	}
}
