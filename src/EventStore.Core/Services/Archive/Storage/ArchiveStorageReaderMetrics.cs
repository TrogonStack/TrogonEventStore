using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive.Naming;

namespace EventStore.Core.Services.Archive.Storage;

public sealed class ArchiveStorageReaderMetrics(
	IArchiveStorageReader inner,
	IArchiveMetrics metrics) : IArchiveStorageReader
{
	public IArchiveChunkNamer ChunkNamer => inner.ChunkNamer;

	public ValueTask<long> GetCheckpoint(CancellationToken ct) => inner.GetCheckpoint(ct);

	public async ValueTask<long> GetChunkLength(string chunkFile, CancellationToken ct) =>
		await MeasureMetadata(
			ArchiveOperation.ReadMetadata,
			() => inner.GetChunkLength(chunkFile, ct));

	public async ValueTask<Stream> GetChunk(string chunkFile, long start, long end, CancellationToken ct)
	{
		var started = Stopwatch.GetTimestamp();
		try
		{
			var stream = await inner.GetChunk(chunkFile, start, end, ct);
			return new ArchiveReadStream(stream, metrics, ArchiveOperation.ReadRange, started);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			RecordFailedRead(ArchiveOperation.ReadRange, started);
			throw;
		}
	}

	public async ValueTask<Stream> GetChunk(string chunkFile, CancellationToken ct)
	{
		var started = Stopwatch.GetTimestamp();
		try
		{
			var stream = await inner.GetChunk(chunkFile, ct);
			return new ArchiveReadStream(stream, metrics, ArchiveOperation.ReadFull, started);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			RecordFailedRead(ArchiveOperation.ReadFull, started);
			throw;
		}
	}

	public async IAsyncEnumerable<string> ListChunks([EnumeratorCancellation] CancellationToken ct)
	{
		await foreach (var chunk in inner.ListChunks(ct).WithCancellation(ct))
		{
			yield return chunk;
		}
	}

	private async ValueTask<T> MeasureMetadata<T>(ArchiveOperation operation, Func<ValueTask<T>> action)
	{
		var started = Stopwatch.GetTimestamp();
		try
		{
			var result = await action();
			metrics.RecordRead(operation, Stopwatch.GetElapsedTime(started), succeeded: true);
			return result;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			metrics.RecordFailure(operation);
			metrics.RecordRead(operation, Stopwatch.GetElapsedTime(started), succeeded: false);
			throw;
		}
	}

	private void RecordFailedRead(ArchiveOperation operation, long started)
	{
		metrics.RecordFailure(operation);
		metrics.RecordRead(operation, Stopwatch.GetElapsedTime(started), succeeded: false);
	}
}
