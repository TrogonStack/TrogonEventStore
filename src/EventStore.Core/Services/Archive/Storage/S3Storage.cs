using Amazon.Runtime;
using FluentStorage;
using FluentStorage.AWS.Blobs;

namespace EventStore.Core.Services.Archive.Storage;

internal static class S3Storage {
	public static IAwsS3BlobStorage Create(S3Options options) {
		if (!string.IsNullOrWhiteSpace(options.ServiceUrl)) {
			var sessionToken = string.IsNullOrWhiteSpace(options.SessionToken) ? null : options.SessionToken;

			return (IAwsS3BlobStorage)StorageFactory.Blobs.AwsS3(
				options.AccessKeyId,
				options.SecretAccessKey,
				sessionToken,
				options.Bucket,
				options.Region,
				options.ServiceUrl);
		}

		if (HasExplicitCredentials(options)) {
			return (IAwsS3BlobStorage)StorageFactory.Blobs.AwsS3(
				CreateCredentials(options),
				options.Bucket,
				options.Region);
		}

		return (IAwsS3BlobStorage)StorageFactory.Blobs.AwsS3(
			bucketName: options.Bucket,
			region: options.Region);
	}

	private static bool HasExplicitCredentials(S3Options options) =>
		!string.IsNullOrWhiteSpace(options.AccessKeyId);

	private static AWSCredentials CreateCredentials(S3Options options) =>
		string.IsNullOrWhiteSpace(options.SessionToken)
			? new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey)
			: new SessionAWSCredentials(options.AccessKeyId, options.SecretAccessKey, options.SessionToken);
}
