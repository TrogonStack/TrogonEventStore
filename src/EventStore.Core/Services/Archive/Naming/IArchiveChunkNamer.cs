namespace EventStore.Core.Services.Archive.Naming;

public interface IArchiveChunkNamer
{
	string GetFileNameFor(int logicalChunkNumber);
}
