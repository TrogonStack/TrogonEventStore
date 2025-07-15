using System.Threading.Tasks;
using Serilog;

namespace EventStore.Core.Services.Archiver.Storage;

public class S3ArchiveStorage(S3Options options) : IArchiveStorage
{
	protected static readonly ILogger Log = Serilog.Log.ForContext<S3ArchiveStorage>();
	private readonly string _bucket = options.Bucket;

	public ValueTask StoreChunk(string pathToSourceChunk)
	{
		Log.Information("Pretending to upload \"{Path}\" to S3 bucket \"{Bucket}\"", pathToSourceChunk, _bucket);
		return ValueTask.CompletedTask;
	}
}
