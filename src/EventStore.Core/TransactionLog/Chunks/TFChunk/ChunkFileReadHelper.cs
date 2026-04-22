using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Exceptions;
using Microsoft.Win32.SafeHandles;

namespace EventStore.Core.TransactionLog.Chunks.TFChunk;

internal static class ChunkFileReadHelper
{
	public static SafeFileHandle OpenValidatedMetadataReadHandle(string fileName, out long length)
	{
		var handle = File.OpenHandle(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
			FileOptions.Asynchronous);
		length = RandomAccess.GetLength(handle);
		if (length >= ChunkFooter.Size + ChunkHeader.Size)
			return handle;

		handle.Dispose();
		throw new CorruptDatabaseException(new BadChunkInDatabaseException(
			$"Chunk file '{fileName}' is bad. It does not have enough size for header and footer. File size is {length} bytes."));
	}

	public static async ValueTask ReadExactlyAsync(SafeFileHandle handle, Memory<byte> buffer, long offset,
		string fileName, CancellationToken token)
	{
		var totalRead = 0;
		while (totalRead < buffer.Length)
		{
			var bytesRead = await RandomAccess.ReadAsync(handle, buffer[totalRead..], offset + totalRead, token);
			if (bytesRead == 0)
			{
				throw new CorruptDatabaseException(new BadChunkInDatabaseException(
					$"Chunk file '{fileName}' was truncated while reading metadata."));
			}

			totalRead += bytesRead;
		}
	}
}
