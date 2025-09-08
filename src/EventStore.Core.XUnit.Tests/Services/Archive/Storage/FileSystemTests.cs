using EventStore.Core.Services.Archive;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Storage;

public class FileSystemReaderTests : ArchiveStorageReaderTests<FileSystemReaderTests>
{
	protected override StorageType StorageType => StorageType.FileSystem;
}

public class FileSystemWriterTests : ArchiveStorageWriterTests<FileSystemWriterTests>
{
	protected override StorageType StorageType => StorageType.FileSystem;
}
