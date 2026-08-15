using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.Services.Archive.Storage.Exceptions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Storage;

public class ArchiveStorageReaderMetricsTests
{
	[Theory]
	[InlineData(ArchiveOperation.ReadMetadata)]
	[InlineData(ArchiveOperation.ReadFull)]
	[InlineData(ArchiveOperation.ReadRange)]
	public async Task records_successful_remote_requests(ArchiveOperation operation)
	{
		var metrics = new RecordingArchiveMetrics();
		var sut = new ArchiveStorageReaderMetrics(new StubReader(), metrics);

		await InvokeAndConsume(sut, metrics, operation);

		var read = Assert.Single(metrics.Reads);
		Assert.Equal(operation, read.Operation);
		Assert.True(read.Succeeded);
		Assert.True(read.Duration >= TimeSpan.Zero);
		Assert.Empty(metrics.Failures);
	}

	[Fact]
	public async Task records_stream_failures_during_remote_content_reads()
	{
		var metrics = new RecordingArchiveMetrics();
		var sut = new ArchiveStorageReaderMetrics(new StubReader(streamFails: true), metrics);
		await using var stream = await sut.GetChunk("chunk", CancellationToken.None);

		Assert.Empty(metrics.Reads);
		await Assert.ThrowsAsync<IOException>(async () =>
			await stream.ReadExactlyAsync(new byte[1], CancellationToken.None));

		Assert.Equal(ArchiveOperation.ReadFull, Assert.Single(metrics.Failures));
		Assert.False(Assert.Single(metrics.Reads).Succeeded);
	}

	[Fact]
	public async Task an_empty_read_does_not_hide_a_later_stream_failure()
	{
		var metrics = new RecordingArchiveMetrics();
		var sut = new ArchiveStorageReaderMetrics(new StubReader(emptyThenFails: true), metrics);
		await using var stream = await sut.GetChunk("chunk", CancellationToken.None);

		Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
		Assert.Empty(metrics.Reads);

		await Assert.ThrowsAsync<IOException>(async () =>
			await stream.ReadExactlyAsync(new byte[1], CancellationToken.None));
		Assert.Equal(ArchiveOperation.ReadFull, Assert.Single(metrics.Failures));
		Assert.False(Assert.Single(metrics.Reads).Succeeded);
	}

	[Theory]
	[InlineData(ArchiveOperation.ReadFull)]
	[InlineData(ArchiveOperation.ReadRange)]
	public async Task missing_chunks_record_an_unsuccessful_read_without_a_storage_failure(ArchiveOperation operation)
	{
		var metrics = new RecordingArchiveMetrics();
		var sut = new ArchiveStorageReaderMetrics(new StubReader(deleted: true), metrics);

		await Assert.ThrowsAsync<ChunkDeletedException>(() => Invoke(sut, operation));

		Assert.Empty(metrics.Failures);
		var read = Assert.Single(metrics.Reads);
		Assert.Equal(operation, read.Operation);
		Assert.False(read.Succeeded);
	}

	[Theory]
	[InlineData(ArchiveOperation.ReadMetadata)]
	[InlineData(ArchiveOperation.ReadFull)]
	[InlineData(ArchiveOperation.ReadRange)]
	public async Task records_failed_remote_requests(ArchiveOperation operation)
	{
		var metrics = new RecordingArchiveMetrics();
		var sut = new ArchiveStorageReaderMetrics(new StubReader(fail: true), metrics);

		await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(sut, operation));

		Assert.Equal(operation, Assert.Single(metrics.Failures));
		var read = Assert.Single(metrics.Reads);
		Assert.Equal(operation, read.Operation);
		Assert.False(read.Succeeded);
	}

	[Theory]
	[InlineData(ArchiveOperation.ReadMetadata)]
	[InlineData(ArchiveOperation.ReadFull)]
	[InlineData(ArchiveOperation.ReadRange)]
	public async Task does_not_record_cancelled_remote_requests_as_failures(ArchiveOperation operation)
	{
		var metrics = new RecordingArchiveMetrics();
		var sut = new ArchiveStorageReaderMetrics(new StubReader(cancel: true), metrics);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(sut, operation));

		Assert.Empty(metrics.Failures);
		Assert.Empty(metrics.Reads);
	}

	[Fact]
	public async Task does_not_record_cancelled_content_reads_as_failures()
	{
		var metrics = new RecordingArchiveMetrics();
		var sut = new ArchiveStorageReaderMetrics(new StubReader(streamCancels: true), metrics);
		await using var stream = await sut.GetChunk("chunk", CancellationToken.None);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			await stream.ReadExactlyAsync(new byte[1], CancellationToken.None));

		Assert.Empty(metrics.Failures);
		Assert.Empty(metrics.Reads);
	}

	[Fact]
	public async Task does_not_record_cancelled_synchronous_disposal_as_a_failure()
	{
		var metrics = new RecordingArchiveMetrics();
		var sut = new ArchiveStorageReaderMetrics(new StubReader(disposeCancels: true), metrics);
		var stream = await sut.GetChunk("chunk", CancellationToken.None);

		Assert.Throws<OperationCanceledException>(stream.Dispose);

		Assert.Empty(metrics.Failures);
		Assert.Empty(metrics.Reads);
	}

	private static async Task Invoke(IArchiveStorageReader reader, ArchiveOperation operation)
	{
		switch (operation)
		{
			case ArchiveOperation.ReadMetadata:
				await reader.GetChunkLength("chunk", CancellationToken.None);
				break;
			case ArchiveOperation.ReadFull:
				await reader.GetChunk("chunk", CancellationToken.None);
				break;
			case ArchiveOperation.ReadRange:
				await reader.GetChunk("chunk", 0, 1, CancellationToken.None);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
		}
	}

	private static async Task InvokeAndConsume(
		IArchiveStorageReader reader,
		RecordingArchiveMetrics metrics,
		ArchiveOperation operation)
	{
		if (operation == ArchiveOperation.ReadMetadata)
		{
			await reader.GetChunkLength("chunk", CancellationToken.None);
			return;
		}

		await using var stream = operation switch
		{
			ArchiveOperation.ReadFull => await reader.GetChunk("chunk", CancellationToken.None),
			ArchiveOperation.ReadRange => await reader.GetChunk("chunk", 0, 1, CancellationToken.None),
			_ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
		};
		Assert.Empty(metrics.Reads);
		await stream.CopyToAsync(Stream.Null);
	}

	private sealed class StubReader(
		bool fail = false,
		bool streamFails = false,
		bool cancel = false,
		bool streamCancels = false,
		bool disposeCancels = false,
		bool emptyThenFails = false,
		bool deleted = false) : IArchiveStorageReader
	{
		public IArchiveChunkNamer ChunkNamer { get; } = new StubChunkNamer();

		public ValueTask<long> GetCheckpoint(CancellationToken ct) => ValueTask.FromResult(0L);

		public ValueTask<long> GetChunkLength(string chunkFile, CancellationToken ct) =>
			cancel
				? ValueTask.FromException<long>(new OperationCanceledException())
				: fail ? ValueTask.FromException<long>(new InvalidOperationException()) : ValueTask.FromResult(1L);

		public ValueTask<Stream> GetChunk(string chunkFile, long start, long end, CancellationToken ct) =>
			GetChunk(chunkFile, ct);

		public ValueTask<Stream> GetChunk(string chunkFile, CancellationToken ct) =>
			cancel
				? ValueTask.FromException<Stream>(new OperationCanceledException())
				: deleted
					? ValueTask.FromException<Stream>(new ChunkDeletedException())
				: fail
				? ValueTask.FromException<Stream>(new InvalidOperationException())
				: ValueTask.FromResult<Stream>(
					streamFails
						? new FailingReadStream()
						: streamCancels
							? new CanceledReadStream()
							: disposeCancels
								? new CanceledDisposeStream()
								: emptyThenFails ? new EmptyThenFailingReadStream() : new MemoryStream([1]));

		public async IAsyncEnumerable<string> ListChunks([EnumeratorCancellation] CancellationToken ct)
		{
			await Task.CompletedTask;
			yield break;
		}
	}

	private sealed class StubChunkNamer : IArchiveChunkNamer
	{
		public string Prefix => "chunk-";
		public string GetFileNameFor(int logicalChunkNumber) => $"chunk-{logicalChunkNumber}";
	}

	private sealed class FailingReadStream : MemoryStream
	{
		public override ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<int>(new IOException("remote read failed"));
	}

	private sealed class CanceledReadStream : MemoryStream
	{
		public override ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromException<int>(new OperationCanceledException());
	}

	private sealed class CanceledDisposeStream : MemoryStream
	{
		protected override void Dispose(bool disposing) => throw new OperationCanceledException();
	}

	private sealed class EmptyThenFailingReadStream : MemoryStream
	{
		public override ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default) =>
			buffer.IsEmpty
				? ValueTask.FromResult(0)
				: ValueTask.FromException<int>(new IOException("remote read failed"));
	}

}
