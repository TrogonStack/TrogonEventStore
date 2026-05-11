using FluentStorage.AWS.Blobs;
using Serilog;

namespace EventStore.Core.Services.Archive.Storage;

public class S3Writer : FluentWriter, IArchiveStorageWriter {
	public S3Writer(S3Options options, string archiveCheckpointFile) : base(
		archiveCheckpointFile) {
		AwsTraceLogging.Configure();
		BlobStorage = S3Storage.Create(options);
	}

	protected override ILogger Log { get; } = Serilog.Log.ForContext<S3Writer>();

	protected override IAwsS3BlobStorage BlobStorage { get; }
}
