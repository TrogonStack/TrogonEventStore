using System.Reflection;
using Amazon.Runtime;
using Amazon.S3;
using EventStore.Common.Exceptions;
using EventStore.Core.Services.Archive;
using EventStore.Core.Services.Archive.Storage;
using FluentStorage.AWS.Blobs;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Storage;

public class S3StorageCredentialTests
{
	[Fact]
	public void native_s3_uses_explicit_credentials_when_configured()
	{
		const string accessKeyId = "explicit-access-key";
		var writer = new InspectableS3Writer(new S3Options
		{
			Bucket = "archive",
			Region = "us-east-1",
			AccessKeyId = accessKeyId,
			SecretAccessKey = "explicit-secret-key",
		});

		var client = Assert.IsType<AmazonS3Client>(writer.Storage.NativeBlobClient);
		var credentials = GetCredentials(client);

		Assert.Equal(accessKeyId, credentials.GetCredentials().AccessKey);
	}

	[Fact]
	public void s3_compatible_storage_without_session_token_uses_basic_credentials()
	{
		var writer = new InspectableS3Writer(new S3Options
		{
			Bucket = "archive",
			Region = "us-east-1",
			ServiceUrl = "https://s3-compatible.example",
			AccessKeyId = "explicit-access-key",
			SecretAccessKey = "explicit-secret-key",
		});

		var client = Assert.IsType<AmazonS3Client>(writer.Storage.NativeBlobClient);
		var credentials = Assert.IsType<BasicAWSCredentials>(GetCredentials(client));

		Assert.True(string.IsNullOrEmpty(credentials.GetCredentials().Token));
	}

	[Fact]
	public void native_s3_rejects_missing_bucket_before_creating_client()
	{
		var exception = Assert.Throws<InvalidConfigurationException>(() => new InspectableS3Writer(new S3Options
		{
			Region = "us-east-1",
		}));

		Assert.Contains("Bucket", exception.Message);
	}

	[Fact]
	public void s3_compatible_storage_rejects_missing_credentials_before_creating_client()
	{
		var exception = Assert.Throws<InvalidConfigurationException>(() => new InspectableS3Writer(new S3Options
		{
			Bucket = "archive",
			Region = "us-east-1",
			ServiceUrl = "https://s3-compatible.example",
		}));

		Assert.Contains("AccessKeyId", exception.Message);
		Assert.Contains("SecretAccessKey", exception.Message);
	}

	private sealed class InspectableS3Writer(S3Options options) : S3Writer(options, "archive.chk")
	{
		public IAwsS3BlobStorage Storage => BlobStorage;
	}

	private static AWSCredentials GetCredentials(AmazonS3Client client) =>
		Assert.IsType<AWSCredentials>(
			typeof(AmazonServiceClient)
				.GetProperty("ExplicitAWSCredentials", BindingFlags.Instance | BindingFlags.NonPublic)!
				.GetValue(client),
			exactMatch: false);
}
