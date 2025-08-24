using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotNext.Buffers;
using EventStore.Core.Services.Archive.Storage.Exceptions;
using FluentStorage.Blobs;
using Serilog;

namespace EventStore.Core.Services.Archive.Storage;

public abstract class FluentReader(string archiveCheckpointFile)
{
	protected abstract ILogger Log { get; }
	protected abstract IBlobStorage BlobStorage { get; }

	public async ValueTask<long> GetCheckpoint(CancellationToken ct)
	{
		await using var stream = await BlobStorage.OpenReadAsync(archiveCheckpointFile, ct);

		if (stream is null)
			return 0L;

		using var buffer = Memory.AllocateExactly<byte>(sizeof(long));
		await stream.ReadExactlyAsync(buffer.Memory, ct);
		var checkpoint = BinaryPrimitives.ReadInt64LittleEndian(buffer.Span);
		return checkpoint;
	}

	public async ValueTask<Stream> GetChunk(string chunkFile, CancellationToken ct)
	{
		var stream = await BlobStorage.OpenReadAsync(chunkFile, ct);
		return stream ?? throw new ChunkDeletedException();
	}

	public abstract IAsyncEnumerable<string> ListChunks(CancellationToken ct);
}
