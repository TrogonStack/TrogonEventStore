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
	public void filesystem_archive_requires_path()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.FileSystem,
			RetainAtLeast = new() {
				Days = 7,
				LogicalBytes = 1024,
			},
		};

		var ex = Assert.Throws<InvalidConfigurationException>(sut.Validate);

		Assert.Contains("Archive configuration", ex.Message);
		Assert.Contains("Path", ex.Message);
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
	public void retention_requires_days()
	{
		var sut = new ArchiveOptions {
			Enabled = true,
			StorageType = StorageType.FileSystem,
			FileSystem = new() {
				Path = "/tmp/archive",
			},
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
			StorageType = StorageType.FileSystem,
			FileSystem = new() {
				Path = "/tmp/archive",
			},
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
			StorageType = StorageType.FileSystem,
			FileSystem = new() {
				Path = "/tmp/archive",
			},
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
			StorageType = StorageType.FileSystem,
			FileSystem = new() {
				Path = "/tmp/archive",
			},
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
			StorageType = StorageType.FileSystem,
			FileSystem = new() {
				Path = "/tmp/archive",
			},
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
}
