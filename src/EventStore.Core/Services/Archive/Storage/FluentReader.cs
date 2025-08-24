using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive.Storage.Exceptions;
using FluentStorage.Blobs;
using Serilog;

namespace EventStore.Core.Services.Archive.Storage;

public abstract class FluentReader
{
	protected abstract ILogger Log { get; }
	protected abstract IBlobStorage BlobStorage { get; }

	public ValueTask<long> GetCheckpoint(CancellationToken ct)
	{
		throw new NotImplementedException();
	}

	public async ValueTask<Stream> GetChunk(string chunkFile, CancellationToken ct)
	{
		var stream = await BlobStorage.OpenReadAsync(chunkFile, ct);
		return stream ?? throw new ChunkDeletedException();
	}

	public IAsyncEnumerable<string> ListChunks(CancellationToken ct)
	{
		throw new NotImplementedException();
	}
}
