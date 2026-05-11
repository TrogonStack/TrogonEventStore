using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.Services.Archive.Storage.Exceptions;

namespace EventStore.Core.Tests.TransactionLog;

public sealed class LocalArchiveStorage(
	string archivePath,
	IArchiveChunkNamer chunkNamer,
	string archiveCheckpointFile)
	: IArchiveStorageReader, IArchiveStorageWriter
{
	public IArchiveChunkNamer ChunkNamer { get; } = chunkNamer;

	public ValueTask<long> GetCheckpoint(CancellationToken ct)
	{
		var checkpointPath = Path.Combine(archivePath, archiveCheckpointFile);
		if (!File.Exists(checkpointPath))
		{
			return ValueTask.FromResult(0L);
		}

		Span<byte> buffer = stackalloc byte[sizeof(long)];
		using var handle = File.OpenHandle(checkpointPath);
		if (RandomAccess.Read(handle, buffer, fileOffset: 0L) != buffer.Length)
		{
			throw new EndOfStreamException();
		}

		return ValueTask.FromResult(BinaryPrimitives.ReadInt64LittleEndian(buffer));
	}

	public ValueTask<long> GetChunkLength(string chunkFile, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();

		try
		{
			return ValueTask.FromResult(new FileInfo(Path.Combine(archivePath, chunkFile)).Length);
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			return ValueTask.FromException<long>(new ChunkDeletedException());
		}
	}

	public ValueTask<Stream> GetChunk(string chunkFile, CancellationToken ct)
	{
		try
		{
			return ValueTask.FromResult<Stream>(File.OpenRead(Path.Combine(archivePath, chunkFile)));
		}
		catch (FileNotFoundException)
		{
			return ValueTask.FromException<Stream>(new ChunkDeletedException());
		}
	}

	public async ValueTask<Stream> GetChunk(string chunkFile, long start, long end, CancellationToken ct)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(start);

		var length = end - start;
		if (length == 0)
		{
			return Stream.Null;
		}

		if (length < 0)
		{
			throw new InvalidOperationException(
				$"Attempted to read negative amount from chunk {chunkFile}. Start: {start}. End {end}");
		}

		var path = Path.Combine(archivePath, chunkFile);
		if (!File.Exists(path))
		{
			throw new ChunkDeletedException();
		}

		var bytes = await File.ReadAllBytesAsync(path, ct);
		if (start >= bytes.Length)
		{
			return Stream.Null;
		}

		var available = bytes.Length - start;
		return new MemoryStream(
			bytes,
			checked((int)start),
			checked((int)Math.Min(length, available)),
			writable: false);
	}

	public async IAsyncEnumerable<string> ListChunks([EnumeratorCancellation] CancellationToken ct)
	{
		foreach (var fileName in Directory.EnumerateFiles(archivePath, $"{ChunkNamer.Prefix}*")
					 .Select(Path.GetFileName)
					 .Order())
		{
			ct.ThrowIfCancellationRequested();
			yield return fileName!;
			await Task.Yield();
		}
	}

	public ValueTask<bool> SetCheckpoint(long checkpoint, CancellationToken ct)
	{
		var checkpointPath = Path.Combine(archivePath, archiveCheckpointFile);
		Span<byte> buffer = stackalloc byte[sizeof(long)];
		BinaryPrimitives.WriteInt64LittleEndian(buffer, checkpoint);
		File.WriteAllBytes(checkpointPath, buffer.ToArray());
		return ValueTask.FromResult(true);
	}

	public async ValueTask<bool> StoreChunk(string chunkPath, string destinationFile, CancellationToken ct)
	{
		if (!File.Exists(chunkPath))
		{
			throw new ChunkDeletedException();
		}

		await using var source = File.OpenRead(chunkPath);
		await using var destination = File.Create(Path.Combine(archivePath, destinationFile));
		await source.CopyToAsync(destination, ct);
		return true;
	}
}
