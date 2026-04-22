namespace EventStore.Core.Data;

public record ChunkInfo
{
	public string ChunkLocator;
	public int ChunkStartNumber;
	public int ChunkEndNumber;
	public long ChunkStartPosition;
	public long ChunkEndPosition;
	public bool IsCompleted;
	public bool IsRemote;
}
