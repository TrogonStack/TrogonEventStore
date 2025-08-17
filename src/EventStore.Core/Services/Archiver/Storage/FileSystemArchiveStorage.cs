using System.IO;
using System.Threading.Tasks;
using Serilog;

namespace EventStore.Core.Services.Archiver.Storage;

public class FileSystemArchiveStorage(FileSystemOptions options) : IArchiveStorage
{
	protected static readonly ILogger Log = Serilog.Log.ForContext<FileSystemArchiveStorage>();

	private readonly string _archivePath = options.Path;

	public ValueTask StoreChunk(string pathToSourceChunk)
	{
		var fileName = Path.GetFileName(pathToSourceChunk);
		var pathToDestinationChunk = Path.Combine(_archivePath, fileName);
		Log.Information("Copying file {Path} to {Destination}", pathToSourceChunk, pathToDestinationChunk);
		File.Copy(pathToSourceChunk, pathToDestinationChunk);
		return ValueTask.CompletedTask;
	}
}
