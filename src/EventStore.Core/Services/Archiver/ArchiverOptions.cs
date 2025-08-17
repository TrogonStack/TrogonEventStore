namespace EventStore.Core.Services.Archiver;

public class ArchiverOptions
{
	public StorageType StorageType { get; init; } = StorageType.None;
	public FileSystemOptions FileSystem { get; init; } = new();
	public S3Options S3 { get; init; } = new();
}

public enum StorageType
{
	None,
	FileSystem,
	S3,
}

public class FileSystemOptions
{
	public string Path { get; init; } = "";
}

public class S3Options
{
	public string Bucket { get; init; } = "";
}
