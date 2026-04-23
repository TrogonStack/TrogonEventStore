using System;

namespace EventStore.Core.Services.Archive.Storage.Exceptions;

public class ChunkDeletedException : Exception
{
	public ChunkDeletedException()
		: base("Chunk has been deleted.")
	{
	}

	public ChunkDeletedException(Exception innerException)
		: base("Chunk has been deleted.", innerException)
	{
	}
}
