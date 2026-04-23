using System;
using EventStore.Core.Services.Archive.Naming;

namespace EventStore.Core.Services.Archive.Storage;

public class ArchiveStorageFactory(
	ArchiveOptions options,
	IArchiveChunkNamer chunkNamer) : IArchiveStorageFactory
{
	private const string ArchiveCheckpointFile = "archive.chk";

	public IArchiveStorageReader CreateReader()
	{
		return options.StorageType switch
		{
			StorageType.Unspecified => throw new InvalidOperationException("Please specify an Archive StorageType"),
			StorageType.FileSystem => new FileSystemReader(options.FileSystem, chunkNamer,
				ArchiveCheckpointFile),
			StorageType.S3 => new S3Reader(options.S3, chunkNamer, ArchiveCheckpointFile),
			_ => throw new ArgumentOutOfRangeException(nameof(options.StorageType))
		};
	}

	public IArchiveStorageWriter CreateWriter()
	{
		return options.StorageType switch
		{
			StorageType.Unspecified => throw new InvalidOperationException("Please specify an Archive StorageType"),
			StorageType.FileSystem => new FileSystemWriter(options.FileSystem, ArchiveCheckpointFile),
			StorageType.S3 => new S3Writer(options.S3, ArchiveCheckpointFile),
			_ => throw new ArgumentOutOfRangeException(nameof(options.StorageType))
		};
	}
}
