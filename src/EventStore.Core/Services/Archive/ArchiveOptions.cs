using System;
using EventStore.Common.Exceptions;

namespace EventStore.Core.Services.Archive;

public class ArchiveOptions
{
	public bool Enabled { get; init; } = false;
	public StorageType StorageType { get; init; } = StorageType.Unspecified;
	public FileSystemOptions FileSystem { get; init; } = new();
	public S3Options S3 { get; init; } = new();
	public RetentionOptions RetainAtLeast { get; init; } = new();

	public void Validate() {
		try {
			if (!Enabled)
				return;

			switch (StorageType) {
				case StorageType.Unspecified:
					throw new InvalidConfigurationException("Please specify an Archive StorageType");
				case StorageType.FileSystem:
					FileSystem.Validate();
					break;
				case StorageType.S3:
					S3.Validate();
					break;
				default:
					throw new InvalidConfigurationException("Unknown StorageType");
			}

			RetainAtLeast.Validate();
		} catch (InvalidConfigurationException ex) {
			throw new InvalidConfigurationException($"Archive configuration: {ex.Message}", ex);
		}
	}
}

public enum StorageType
{
	Unspecified,
	FileSystem,
	S3,
}

public class RetentionOptions
{
	public long Days { get; init; } = TimeSpan.MaxValue.Days;
	public long LogicalBytes { get; init; } = long.MaxValue;

	public void Validate() {
		if (Days == TimeSpan.MaxValue.Days)
			throw new InvalidConfigurationException("Please specify a value for Days to retain");
		if (Days < 0 || Days > TimeSpan.MaxValue.Days)
			throw new InvalidConfigurationException($"Days must be between 0 and {TimeSpan.MaxValue.Days}");

		if (LogicalBytes == long.MaxValue)
			throw new InvalidConfigurationException("Please specify a value for LogicalBytes to retain");
		if (LogicalBytes < 0)
			throw new InvalidConfigurationException("LogicalBytes must be greater than or equal to 0");
	}
}

public class FileSystemOptions
{
	public string Path { get; init; } = "";

	public void Validate() {
		if (string.IsNullOrWhiteSpace(Path))
			throw new InvalidConfigurationException("Please provide a Path for the FileSystem archive");
	}
}

public class S3Options
{
	public string Bucket { get; init; } = "";
	public string Region { get; init; } = "";

	public void Validate() {
		if (string.IsNullOrWhiteSpace(Bucket))
			throw new InvalidConfigurationException("Please provide a Bucket for the S3 archive");

		if (string.IsNullOrWhiteSpace(Region))
			throw new InvalidConfigurationException("Please provide a Region for the S3 archive");
	}
}
