namespace EventStore.Core.Services.Archive.Archiver;

public static class Commands
{
	public record ArchiveChunk : Command
	{
		public string ChunkPath;
		public int ChunkStartNumber;
		public int ChunkEndNumber;
		public long ChunkEndPosition;
	}
}
