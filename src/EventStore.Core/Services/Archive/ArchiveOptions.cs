using System;

namespace EventStore.Core.Services.Archive;

public class ArchiveOptions
{
	public bool Enabled { get; init; } = false;
	public StorageType StorageType { get; init; } = StorageType.Unspecified;
	public FileSystemOptions FileSystem { get; init; } = new();
	public S3Options S3 { get; init; } = new();
	public RetentionOptions RetainAtLeast { get; init; } = new();
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
}

public class FileSystemOptions
{
	public string Path { get; init; } = "";
}

public class S3Options
{
	public string AwsCliProfileName { get; init; } = "default";
	public string Bucket { get; init; } = "";
	public string Region { get; init; } = "";
}
