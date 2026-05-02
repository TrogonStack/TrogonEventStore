using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotNext.Buffers;
using EventStore.Core.Exceptions;
using EventStore.Core.TransactionLog.FileNamingStrategy;

namespace EventStore.Core.TransactionLog.Chunks.TFChunk;

public sealed class ChunkLocalFileSystem(IVersionedFileNamingStrategy namingStrategy) : IChunkFileSystem
{
	public IVersionedFileNamingStrategy NamingStrategy { get; } =
		namingStrategy ?? throw new ArgumentNullException(nameof(namingStrategy));

	public ValueTask<IChunkHandle> OpenForReadAsync(string fileName, ReadOptimizationHint readOptimizationHint,
		bool asyncIO,
		CancellationToken token)
	{
		token.ThrowIfCancellationRequested();

		try
		{
			var options = readOptimizationHint switch
			{
				ReadOptimizationHint.None => FileOptions.None,
				ReadOptimizationHint.RandomAccess => FileOptions.RandomAccess,
				ReadOptimizationHint.SequentialScan => FileOptions.SequentialScan,
				_ => throw new ArgumentOutOfRangeException(nameof(readOptimizationHint), readOptimizationHint, null)
			};
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
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			return ValueTask.FromException<IChunkHandle>(
				new CorruptDatabaseException(new ChunkNotFoundException(fileName)));
		}
	}

	public async ValueTask<ChunkHeader> ReadHeaderAsync(string fileName, CancellationToken token)
	{
		using var handle = ChunkFileReadHelper.OpenValidatedMetadataReadHandle(fileName, out _);

		using var buffer = Memory.AllocateExactly<byte>(ChunkHeader.Size);
		await ChunkFileReadHelper.ReadExactlyAsync(handle, buffer.Memory, 0L, fileName, token);
		return new(buffer.Span);
	}

	public async ValueTask<ChunkFooter> ReadFooterAsync(string fileName, CancellationToken token)
	{
		using var handle = ChunkFileReadHelper.OpenValidatedMetadataReadHandle(fileName, out var length);

		using var buffer = Memory.AllocateExactly<byte>(ChunkFooter.Size);
		await ChunkFileReadHelper.ReadExactlyAsync(handle, buffer.Memory, length - ChunkFooter.Size, fileName, token);
		return new(buffer.Span);
	}

	public IChunkEnumerator CreateChunkEnumerator() => new TFChunkEnumerator(NamingStrategy, ReadHeaderAsync);

	public void MoveFile(string sourceFileName, string destinationFileName) =>
		File.Move(sourceFileName, destinationFileName);

	public void DeleteFile(string fileName) =>
		File.Delete(fileName);

	public void SetAttributes(string fileName, FileAttributes fileAttributes) =>
		File.SetAttributes(fileName, fileAttributes);
}
