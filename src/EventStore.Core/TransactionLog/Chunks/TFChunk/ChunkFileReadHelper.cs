using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Exceptions;
using DotNext.Buffers;
using Microsoft.Win32.SafeHandles;

namespace EventStore.Core.TransactionLog.Chunks.TFChunk;

internal static class ChunkFileReadHelper
{
	public static SafeFileHandle OpenValidatedMetadataReadHandle(string fileName, out long length)
	{
		SafeFileHandle handle = null;
		try
		{
			handle = File.OpenHandle(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
				FileOptions.Asynchronous);
			length = RandomAccess.GetLength(handle);
			ValidateMetadataLength(length, fileName);
			return handle;
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			handle?.Dispose();
			length = 0;
			throw new CorruptDatabaseException(new ChunkNotFoundException(fileName));
		}
		catch
		{
			handle?.Dispose();
			throw;
		}
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

	public static async ValueTask ReadExactlyAsync(IChunkHandle handle, Memory<byte> buffer, long offset,
		string fileName, CancellationToken token)
	{
		var totalRead = 0;
		while (totalRead < buffer.Length)
		{
			var bytesRead = await handle.ReadAsync(buffer[totalRead..], offset + totalRead, token);
			if (bytesRead == 0)
			{
				throw new CorruptDatabaseException(new BadChunkInDatabaseException(
					$"Chunk file '{fileName}' was truncated while reading metadata."));
			}

			totalRead += bytesRead;
		}
	}

	public static async ValueTask<ChunkHeader> ReadHeaderAsync(IChunkHandle handle, string fileName,
		CancellationToken token)
	{
		ValidateMetadataLength(handle.Length, fileName);

		using var buffer = Memory.AllocateExactly<byte>(ChunkHeader.Size);
		await ReadExactlyAsync(handle, buffer.Memory, 0L, fileName, token);
		return new(buffer.Span);
	}

	public static async ValueTask<ChunkFooter> ReadFooterAsync(IChunkHandle handle, string fileName,
		CancellationToken token)
	{
		ValidateMetadataLength(handle.Length, fileName);

		using var buffer = Memory.AllocateExactly<byte>(ChunkFooter.Size);
		await ReadExactlyAsync(handle, buffer.Memory, handle.Length - ChunkFooter.Size, fileName, token);
		return new(buffer.Span);
	}

	private static void ValidateMetadataLength(long length, string fileName)
	{
		if (length >= ChunkFooter.Size + ChunkHeader.Size)
			return;

		throw new CorruptDatabaseException(new BadChunkInDatabaseException(
			$"Chunk file '{fileName}' is bad. It does not have enough size for header and footer. File size is {length} bytes."));
	}
}
