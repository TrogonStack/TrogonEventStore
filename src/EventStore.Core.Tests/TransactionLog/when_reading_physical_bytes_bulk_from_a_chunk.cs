using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DotNext.Threading;
using EventStore.Core.Tests.Transforms.WithHeader;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class when_reading_physical_bytes_bulk_from_a_chunk : SpecificationWithDirectory
{
	[Test]
	public async Task the_file_will_not_be_deleted_until_reader_released()
	{
		var chunk = await TFChunkHelper.CreateNewChunk(GetFilePathFor("file1"), 2000);
		using (var reader = await chunk.AcquireRawReader())
		{
			chunk.MarkForDeletion();
			var buffer = new byte[1024];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsFalse(result.IsEOF);
			Assert.AreEqual(1024, result.BytesRead);
		}

		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task a_read_on_new_file_can_be_performed()
	{
		var chunk = await TFChunkHelper.CreateNewChunk(GetFilePathFor("file1"), 2000);
		using (var reader = await chunk.AcquireRawReader())
		{
			var buffer = new byte[1024];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsFalse(result.IsEOF);
			Assert.AreEqual(1024, result.BytesRead);
		}

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}
	/*
			[Test]
			public void a_read_on_scavenged_chunk_includes_map()
			{
				var chunk = TFChunk.CreateNew(GetFilePathFor("afile"), 200, 0, 0, isScavenged: true, inMem: false, unbuffered: false, writethrough: false);
				chunk.CompleteScavenge(new [] {new PosMap(0, 0), new PosMap(1,1) }, false);
				using (var reader = chunk.AcquireRawReader())
				{
					var buffer = new byte[1024];
					var result = reader.ReadNextBytes(1024, buffer);
					Assert.IsFalse(result.IsEOF);
					Assert.AreEqual(ChunkHeader.Size + ChunkHeader.Size + 2 * PosMap.FullSize, result.BytesRead);
				}
				chunk.MarkForDeletion();
				chunk.WaitForDestroy(5000);
			}

			[Test]
			public void a_read_past_end_of_completed_chunk_does_include_header_or_footer()
			{
				var chunk = TFChunk.CreateNew(GetFilePathFor("File1"), 300, 0, 0, isScavenged: false, inMem: false, unbuffered: false, writethrough: false);
				chunk.Complete();
				using (var reader = chunk.AcquireRawReader())
				{
					var buffer = new byte[1024];
					var result = reader.ReadNextBytes(1024, buffer);
					Assert.IsTrue(result.IsEOF);
					Assert.AreEqual(ChunkHeader.Size + ChunkFooter.Size, result.BytesRead); //just header + footer = 256
				}
				chunk.MarkForDeletion();
				chunk.WaitForDestroy(5000);
			}
	*/

	[Test]
	public async Task if_asked_for_more_than_buffer_size_will_only_read_buffer_size()
	{
		var chunk = await TFChunkHelper.CreateNewChunk(GetFilePathFor("file1"), 3000);
		using (var reader = await chunk.AcquireRawReader())
		{
			var buffer = new byte[1024];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsFalse(result.IsEOF);
			Assert.AreEqual(1024, result.BytesRead);
		}

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task a_read_past_eof_returns_eof_and_no_footer()
	{
		var chunk = await TFChunkHelper.CreateNewChunk(GetFilePathFor("file1"), 300);
		using (var reader = await chunk.AcquireRawReader())
		{
			var buffer = new byte[8092];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsTrue(result.IsEOF);
			Assert.AreEqual(4096, result.BytesRead); //does not includes header and footer space
		}

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task a_raw_read_on_completed_transformed_chunk_falls_back_from_stale_cache()
	{
		var chunk = await TFChunk.CreateNew(
			fileSystem: TFChunkHelper.CreateLocalFileSystem(GetFilePathFor("file1")),
			filename: GetFilePathFor("file1"),
			chunkDataSize: 2000,
			chunkStartNumber: 0,
			chunkEndNumber: 0,
			isScavenged: false,
			inMem: false,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			asyncIO: false,
			tracker: new TFChunkTracker.NoOp(),
			transformFactory: new WithHeaderChunkTransformFactory(),
			token: CancellationToken.None);
		await chunk.Complete(CancellationToken.None);

		using (var reader = await chunk.AcquireRawReader())
		{
			Assert.IsFalse(reader.IsMemory);

			var buffer = new byte[1024];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsFalse(result.IsEOF);
			Assert.Greater(result.BytesRead, 0);
		}

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task a_dedicated_raw_reader_on_completed_chunk_uses_the_chunk_file_system()
	{
		var fileSystem = new ObservingChunkFileSystem(
			new ChunkLocalFileSystem(new VersionedPatternFileNamingStrategy(PathName, "chunk-")));
		var chunk = await TFChunk.CreateNew(
			fileSystem: fileSystem,
			filename: GetFilePathFor("file1"),
			chunkDataSize: 2000,
			chunkStartNumber: 0,
			chunkEndNumber: 0,
			isScavenged: false,
			inMem: false,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			asyncIO: false,
			tracker: new TFChunkTracker.NoOp(),
			transformFactory: new WithHeaderChunkTransformFactory(),
			token: CancellationToken.None);
		await chunk.Complete(CancellationToken.None);
		chunk.UnCacheFromMemory();
		var bulkReaderOpenCount = fileSystem.BulkReaderOpenHints.Count;

		using (var reader = await chunk.AcquireRawReader())
		{
			Assert.IsFalse(reader.IsMemory);
			Assert.That(fileSystem.BulkReaderOpenHints.Count, Is.EqualTo(bulkReaderOpenCount + 1));
			Assert.That(fileSystem.BulkReaderOpenHints[^1], Is.EqualTo(ReadOptimizationHint.SequentialScan));
			Assert.That(fileSystem.BulkReaderOpenAsyncFlags[^1], Is.False);

			var buffer = new byte[1024];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsFalse(result.IsEOF);
			Assert.Greater(result.BytesRead, 0);
		}

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task a_dedicated_raw_reader_on_completed_chunk_keeps_bulk_reader_opens_synchronous_even_when_async_io_is_enabled()
	{
		var fileSystem = new ObservingChunkFileSystem(
			new ChunkLocalFileSystem(new VersionedPatternFileNamingStrategy(PathName, "chunk-")));
		var chunk = await TFChunk.CreateNew(
			fileSystem: fileSystem,
			filename: GetFilePathFor("file1"),
			chunkDataSize: 2000,
			chunkStartNumber: 0,
			chunkEndNumber: 0,
			isScavenged: false,
			inMem: false,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			asyncIO: true,
			tracker: new TFChunkTracker.NoOp(),
			transformFactory: new WithHeaderChunkTransformFactory(),
			token: CancellationToken.None);
		await chunk.Complete(CancellationToken.None);
		chunk.UnCacheFromMemory();

		using (var reader = await chunk.AcquireRawReader())
		{
			Assert.IsFalse(reader.IsMemory);
			Assert.That(fileSystem.BulkReaderOpenHints[^1], Is.EqualTo(ReadOptimizationHint.SequentialScan));
			Assert.That(fileSystem.BulkReaderOpenAsyncFlags[^1], Is.False);
		}

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task a_dedicated_raw_reader_on_completed_chunk_propagates_open_cancellation()
	{
		var openStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var fileSystem = new BlockingBulkReaderOpenChunkFileSystem(
			new ChunkLocalFileSystem(new VersionedPatternFileNamingStrategy(PathName, "chunk-")),
			openStarted);
		var chunk = await TFChunk.CreateNew(
			fileSystem: fileSystem,
			filename: GetFilePathFor("file1"),
			chunkDataSize: 2000,
			chunkStartNumber: 0,
			chunkEndNumber: 0,
			isScavenged: false,
			inMem: false,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			asyncIO: false,
			tracker: new TFChunkTracker.NoOp(),
			transformFactory: new WithHeaderChunkTransformFactory(),
			token: CancellationToken.None);
		await chunk.Complete(CancellationToken.None);
		chunk.UnCacheFromMemory();
		fileSystem.BlockSequentialScanOpens = true;

		using var cancellationTokenSource = new CancellationTokenSource();
		var acquireTask = chunk.AcquireRawReader(cancellationTokenSource.Token).AsTask();
		await openStarted.Task;
		cancellationTokenSource.Cancel();

		Assert.That(async () => await acquireTask, Throws.InstanceOf<OperationCanceledException>());

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task a_dedicated_raw_reader_on_completed_in_memory_chunk_waits_for_a_real_mem_reader()
	{
		var chunk = await TFChunk.CreateNew(
			fileSystem: TFChunkHelper.CreateLocalFileSystem(GetFilePathFor("file1")),
			filename: GetFilePathFor("file1"),
			chunkDataSize: 2000,
			chunkStartNumber: 0,
			chunkEndNumber: 0,
			isScavenged: false,
			inMem: true,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			asyncIO: false,
			tracker: new TFChunkTracker.NoOp(),
			transformFactory: new WithHeaderChunkTransformFactory(),
			token: CancellationToken.None);
		await chunk.Complete(CancellationToken.None);

		var cachedDataLock = (AsyncExclusiveLock)typeof(TFChunk)
			.GetField("_cachedDataLock", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(chunk)!;

		cachedDataLock.TryAcquire(System.Threading.Timeout.InfiniteTimeSpan);
		try
		{
			var acquireTask = Task.Run(async () => await chunk.AcquireRawReader());
			await Task.Delay(100);
			Assert.That(acquireTask.IsCompleted, Is.False);

			cachedDataLock.Release();

			using var reader = await acquireTask;
			Assert.IsTrue(reader.IsMemory);

			var buffer = new byte[1024];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsFalse(result.IsEOF);
			Assert.Greater(result.BytesRead, 0);
		}
		finally
		{
			if (cachedDataLock.IsLockHeld)
				cachedDataLock.Release();
			chunk.Dispose();
		}
	}

	private sealed class ObservingChunkFileSystem(IChunkFileSystem inner) : IChunkFileSystem
	{
		public List<ReadOptimizationHint> BulkReaderOpenHints { get; } = [];
		public List<bool> BulkReaderOpenAsyncFlags { get; } = [];

		public IVersionedFileNamingStrategy NamingStrategy => inner.NamingStrategy;

		public async ValueTask<IChunkHandle> OpenForReadAsync(string fileName, ReadOptimizationHint readOptimizationHint,
			bool asyncIO, CancellationToken token)
		{
			var handle = await inner.OpenForReadAsync(fileName, readOptimizationHint, asyncIO, token);
			if (readOptimizationHint == ReadOptimizationHint.SequentialScan)
			{
				BulkReaderOpenHints.Add(readOptimizationHint);
				BulkReaderOpenAsyncFlags.Add(asyncIO);
			}

			return handle;
		}

		public ValueTask<ChunkHeader> ReadHeaderAsync(string fileName, CancellationToken token) =>
			inner.ReadHeaderAsync(fileName, token);

		public ValueTask<ChunkFooter> ReadFooterAsync(string fileName, CancellationToken token) =>
			inner.ReadFooterAsync(fileName, token);

		public IChunkEnumerator CreateChunkEnumerator() => inner.CreateChunkEnumerator();

		public void MoveFile(string sourceFileName, string destinationFileName) =>
			inner.MoveFile(sourceFileName, destinationFileName);

		public void DeleteFile(string fileName) =>
			inner.DeleteFile(fileName);

		public void SetAttributes(string fileName, FileAttributes fileAttributes) =>
			inner.SetAttributes(fileName, fileAttributes);
		}

	private sealed class BlockingBulkReaderOpenChunkFileSystem(
		IChunkFileSystem inner,
		TaskCompletionSource openStarted) : IChunkFileSystem
	{
		public bool BlockSequentialScanOpens { get; set; }

		public IVersionedFileNamingStrategy NamingStrategy => inner.NamingStrategy;

		public async ValueTask<IChunkHandle> OpenForReadAsync(string fileName, ReadOptimizationHint readOptimizationHint,
			bool asyncIO, CancellationToken token)
		{
			if (BlockSequentialScanOpens && readOptimizationHint == ReadOptimizationHint.SequentialScan)
			{
				openStarted.TrySetResult();
				await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
			}

			return await inner.OpenForReadAsync(fileName, readOptimizationHint, asyncIO, token);
		}

		public ValueTask<ChunkHeader> ReadHeaderAsync(string fileName, CancellationToken token) =>
			inner.ReadHeaderAsync(fileName, token);

			public ValueTask<ChunkFooter> ReadFooterAsync(string fileName, CancellationToken token) =>
				inner.ReadFooterAsync(fileName, token);

			public IChunkEnumerator CreateChunkEnumerator() => inner.CreateChunkEnumerator();

			public void MoveFile(string sourceFileName, string destinationFileName) =>
				inner.MoveFile(sourceFileName, destinationFileName);

			public void DeleteFile(string fileName) =>
				inner.DeleteFile(fileName);

			public void SetAttributes(string fileName, FileAttributes fileAttributes) =>
				inner.SetAttributes(fileName, fileAttributes);
		}
	}
