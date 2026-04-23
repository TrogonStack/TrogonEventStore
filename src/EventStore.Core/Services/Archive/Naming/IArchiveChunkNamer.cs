namespace EventStore.Core.Services.Archive.Naming;

public interface IArchiveChunkNamer
{
	string Prefix { get; }
	string GetFileNameFor(int logicalChunkNumber);
}
