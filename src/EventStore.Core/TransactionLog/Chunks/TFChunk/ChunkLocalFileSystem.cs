using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotNext.Buffers;
using EventStore.Core.Exceptions;
using EventStore.Core.TransactionLog.FileNamingStrategy;

namespace EventStore.Core.TransactionLog.Chunks.TFChunk;

public sealed class ChunkLocalFileSystem(IVersionedFileNamingStrategy namingStrategy) : IChunkFileSystem
{
	public IVersionedFileNamingStrategy NamingStrategy { get; } = namingStrategy;

	public ValueTask<IChunkHandle> OpenForReadAsync(string fileName, bool reduceFileCachePressure, bool asyncIO,
		CancellationToken token)
	{
		token.ThrowIfCancellationRequested();

		try
		{
			var options = reduceFileCachePressure ? FileOptions.None : FileOptions.RandomAccess;
			if (asyncIO)
				options |= FileOptions.Asynchronous;

			return ValueTask.FromResult<IChunkHandle>(new ChunkFileHandle(fileName, new FileStreamOptions
			{
				Mode = FileMode.Open,
				Access = FileAccess.Read,
				Share = FileShare.ReadWrite,
				Options = options,
			}));
		}
		catch (FileNotFoundException)
		{
			return ValueTask.FromException<IChunkHandle>(
				new CorruptDatabaseException(new ChunkNotFoundException(fileName)));
		}
	}

	public async ValueTask<ChunkHeader> ReadHeaderAsync(string fileName, CancellationToken token)
	{
		using var handle = File.OpenHandle(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
			FileOptions.Asynchronous);
		var length = RandomAccess.GetLength(handle);
		if (length < ChunkFooter.Size + ChunkHeader.Size)
		{
			throw new CorruptDatabaseException(new BadChunkInDatabaseException(
				$"Chunk file '{fileName}' is bad. It does not have enough size for header and footer. File size is {length} bytes."));
		}

		using var buffer = Memory.AllocateExactly<byte>(ChunkHeader.Size);
		await RandomAccess.ReadAsync(handle, buffer.Memory, 0L, token);
		return new(buffer.Span);
	}

	public async ValueTask<ChunkFooter> ReadFooterAsync(string fileName, CancellationToken token)
	{
		using var handle = File.OpenHandle(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
			FileOptions.Asynchronous);
		var length = RandomAccess.GetLength(handle);
		if (length < ChunkFooter.Size + ChunkHeader.Size)
		{
			throw new CorruptDatabaseException(new BadChunkInDatabaseException(
				$"Chunk file '{fileName}' is bad. It does not have enough size for header and footer. File size is {length} bytes."));
		}

		using var buffer = Memory.AllocateExactly<byte>(ChunkFooter.Size);
		await RandomAccess.ReadAsync(handle, buffer.Memory, length - ChunkFooter.Size, token);
		return new(buffer.Span);
	}

	public IAsyncEnumerable<TFChunkInfo> EnumerateChunks(int lastChunkNumber, CancellationToken token) =>
		new TFChunkEnumerator(NamingStrategy).EnumerateChunks(lastChunkNumber, token: token);
}
