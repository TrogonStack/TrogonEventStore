using System;
using EventStore.Core.Services.Archive.Naming;

namespace EventStore.Core.Services.Archive.Storage;

public class ArchiveStorageFactory(
	ArchiveOptions options,
	IArchiveChunkNamer chunkNamer,
	IArchiveMetrics archiveMetrics = null) : IArchiveStorageFactory
{
	private const string ArchiveCheckpointFile = "archive.chk";
	private readonly IArchiveMetrics _archiveMetrics = archiveMetrics ?? IArchiveMetrics.NoOp;

	public IArchiveStorageReader CreateReader()
	{
		var reader = options.StorageType switch
		{
			StorageType.Unspecified => throw new InvalidOperationException("Please specify an Archive StorageType"),
			StorageType.S3 => new S3Reader(options.S3, chunkNamer, ArchiveCheckpointFile),
			_ => throw new ArgumentOutOfRangeException(nameof(options.StorageType))
		};

		return new ArchiveStorageReaderMetrics(reader, _archiveMetrics);
	}

	public IArchiveStorageWriter CreateWriter()
	{
		return options.StorageType switch
		{
			StorageType.Unspecified => throw new InvalidOperationException("Please specify an Archive StorageType"),
			StorageType.S3 => new S3Writer(options.S3, ArchiveCheckpointFile),
			_ => throw new ArgumentOutOfRangeException(nameof(options.StorageType))
		};
	}
}
