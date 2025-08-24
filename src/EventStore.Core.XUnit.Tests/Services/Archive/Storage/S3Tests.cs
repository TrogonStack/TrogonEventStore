using EventStore.Core.Services.Archive;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Storage;

#if RUN_S3_TESTS
public class S3ReaderTests : ArchiveStorageReaderTests<S3ReaderTests>
{
	protected override StorageType StorageType => StorageType.S3;
}

public class S3WriterTests : ArchiveStorageWriterTests<S3WriterTests>
{
	protected override StorageType StorageType => StorageType.S3;
}
#endif
