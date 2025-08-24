using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using EventStore.Core.Services.Archive.Storage.Exceptions;
using System.Threading.Tasks;
using Serilog;
using FluentStorage.Blobs;

namespace EventStore.Core.Services.Archive.Storage;

public abstract class FluentWriter(string archiveCheckpointFile)
{
	protected abstract ILogger Log { get; }
	protected abstract IBlobStorage BlobStorage { get; }

	private readonly byte[] _buffer = new byte[8];

	// not thread safe
	public async ValueTask<bool> SetCheckpoint(long checkpoint, CancellationToken ct)
	{
		try
		{
			BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(0, 8), checkpoint);
			await BlobStorage.WriteAsync(archiveCheckpointFile, _buffer, append: false, ct);
			return true;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error while setting checkpoint to: {checkpoint}", checkpoint);
			return false;
		}
	}

	public async ValueTask<bool> StoreChunk(string chunkPath, string destinationFile, CancellationToken ct)
	{
		try
		{
			await BlobStorage.WriteFileAsync(destinationFile, filePath: chunkPath, ct);
			return true;
		}
		catch (FileNotFoundException)
		{
			throw new ChunkDeletedException();
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error while storing chunk: {chunkFile}", destinationFile);
			return false;
		}
	}
}
