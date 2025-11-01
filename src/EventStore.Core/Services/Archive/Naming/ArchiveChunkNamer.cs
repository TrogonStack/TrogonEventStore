using System;
using System.IO;
using EventStore.Core.TransactionLog.FileNamingStrategy;

namespace EventStore.Core.Services.Archive.Naming;

public class ArchiveChunkNamer(IVersionedFileNamingStrategy namingStrategy) : IArchiveChunkNamer
{
	public string GetFileNameFor(int logicalChunkNumber)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(logicalChunkNumber);

		var filePath = namingStrategy.GetFilenameFor(logicalChunkNumber, version: 1);
		return Path.GetFileName(filePath);
	}
}
