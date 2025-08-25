using System;
using System.Threading.Tasks;

namespace EventStore.Core.Services.Archiver.Storage;

public interface IArchiveStorage
{
	public static IArchiveStorage None = new NoArchiveStorage();
	public ValueTask StoreChunk(string path);
}

file class NoArchiveStorage : IArchiveStorage
{
	public ValueTask StoreChunk(string path)
	{
		throw new InvalidOperationException();
	}
}
