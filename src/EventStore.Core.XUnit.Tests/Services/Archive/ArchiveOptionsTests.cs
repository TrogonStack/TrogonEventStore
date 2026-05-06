using System;
using EventStore.Common.Exceptions;
using EventStore.Core.Services.Archive;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive;

public class ArchiveOptionsTests
{
	[Fact]
	public void disabled_archive_does_not_require_settings()
	{
		var sut = new ArchiveOptions();

		sut.Validate();
	}

	[Fact]
	public void enabled_archive_requires_storage_type()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			RetainAtLeast = new() {
				Days = 7,
				LogicalBytes = 1024,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("StorageType", ex.Message);
	}

	[Fact]
	public void s3_archive_requires_bucket()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.S3,
			S3 = new() {
				Region = "us-east-1",
			},
			RetainAtLeast = new() {
				Days = 7,
				LogicalBytes = 1024,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("Bucket", ex.Message);
	}

	[Fact]
	public void s3_archive_requires_region()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.S3,
			S3 = new() {
				Bucket = "bucket",
			},
			RetainAtLeast = new() {
				Days = 7,
				LogicalBytes = 1024,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("Region", ex.Message);
	}

	[Fact]
	public void s3_compatible_archive_requires_credentials()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.S3,
			S3 = new() {
				Bucket = "bucket",
				Region = "us-east-1",
				ServiceUrl = "https://s3-compatible.example",
			},
			RetainAtLeast = new() {
				Days = 7,
				LogicalBytes = 1024,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("AccessKeyId", ex.Message);
		Assert.Contains("SecretAccessKey", ex.Message);
	}

	[Fact]
	public void s3_credentials_must_be_configured_together()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.S3,
			S3 = new() {
				Bucket = "bucket",
				Region = "us-east-1",
				AccessKeyId = "access",
			},
			RetainAtLeast = new() {
				Days = 7,
				LogicalBytes = 1024,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("AccessKeyId", ex.Message);
		Assert.Contains("SecretAccessKey", ex.Message);
	}

	[Fact]
	public void s3_session_token_requires_credentials()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.S3,
			S3 = new() {
				Bucket = "bucket",
				Region = "us-east-1",
				SessionToken = "session",
			},
			RetainAtLeast = new() {
				Days = 7,
				LogicalBytes = 1024,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("AccessKeyId", ex.Message);
		Assert.Contains("SecretAccessKey", ex.Message);
	}

	[Fact]
	public void s3_archive_allows_explicit_credentials_without_service_url()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.S3,
			S3 = new() {
				Bucket = "bucket",
				Region = "us-east-1",
				AccessKeyId = "access",
				SecretAccessKey = "secret",
			},
			RetainAtLeast = new() {
				Days = 7,
				LogicalBytes = 1024,
			},
		};

		sut.Validate();
	}

	[Fact]
	public void retention_requires_days()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.S3,
			S3 = ValidS3,
			RetainAtLeast = new() {
				LogicalBytes = 1024,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("Days", ex.Message);
	}

	[Fact]
	public void retention_requires_logical_bytes()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.S3,
			S3 = ValidS3,
			RetainAtLeast = new() {
				Days = 7,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("LogicalBytes", ex.Message);
	}

	[Fact]
	public void retention_rejects_negative_days()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.S3,
			S3 = ValidS3,
			RetainAtLeast = new() {
				Days = -1,
				LogicalBytes = 1024,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("Days", ex.Message);
	}

	[Fact]
	public void retention_rejects_days_above_timespan_limit()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.S3,
			S3 = ValidS3,
			RetainAtLeast = new() {
				Days = TimeSpan.MaxValue.Days + 1L,
				LogicalBytes = 1024,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("Days", ex.Message);
	}

	[Fact]
	public void retention_rejects_negative_logical_bytes()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.S3,
			S3 = ValidS3,
			RetainAtLeast = new() {
				Days = 7,
				LogicalBytes = -1,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("LogicalBytes", ex.Message);
	}

	[Fact]
	public void unknown_storage_type_is_rejected()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = (StorageType)999,
			RetainAtLeast = new() {
				Days = 7,
				LogicalBytes = 1024,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("Unknown StorageType", ex.Message);
	}

	private static S3Options ValidS3 => new() {
		Bucket = "archive",
		Region = "us-east-1",
	};
}
