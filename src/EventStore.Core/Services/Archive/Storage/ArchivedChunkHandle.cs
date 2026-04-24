using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;

namespace EventStore.Core.Services.Archive.Storage;

internal sealed class ArchivedChunkHandle : IChunkHandle
{
	private readonly IArchiveStorageReader _reader;
	private readonly string _chunkFile;

	private ArchivedChunkHandle(IArchiveStorageReader reader, string chunkFile, long length)
	{
		_reader = reader;
		_chunkFile = chunkFile;
		Length = length;
	}

	public static async ValueTask<IChunkHandle> OpenForReadAsync(
		IArchiveStorageReader reader,
		int logicalChunkNumber,
		CancellationToken token)
	{
		var chunkFile = reader.ChunkNamer.GetFileNameFor(logicalChunkNumber);
		var length = await reader.GetChunkLength(chunkFile, token);
		return new ArchivedChunkHandle(reader, chunkFile, length);
	}

	public long Length { get; set; }

	public string Name => _chunkFile;

	public FileAccess Access => FileAccess.Read;

	public void Flush()
	{
	}

	public Task FlushAsync(CancellationToken token) =>
		token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;

	public ValueTask WriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken token) =>
		ValueTask.FromException(new NotSupportedException());

	public async ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken token)
	{
		await using var stream = await _reader.GetChunk(_chunkFile, offset, offset + buffer.Length, token);

		var totalRead = 0;
		while (totalRead < buffer.Length)
		{
			var bytesRead = await stream.ReadAsync(buffer[totalRead..], token);
			if (bytesRead == 0)
				break;

			totalRead += bytesRead;
		}

		return totalRead;
	}

	public ValueTask SetReadOnlyAsync(bool value, CancellationToken token) =>
		token.IsCancellationRequested ? ValueTask.FromCanceled(token) : ValueTask.CompletedTask;

	public void Dispose()
	{
	}
}
