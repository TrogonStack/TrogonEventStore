using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.LogAbstraction;
using EventStore.Core.LogV2;
using EventStore.Core.Transforms.Identity;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Plugins.Transforms;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class when_reading_logical_bytes_bulk_from_a_chunk<TLogFormat, TStreamId> : SpecificationWithDirectory
{
	[Test]
	public async Task the_file_will_not_be_deleted_until_reader_released()
	{
		var chunk = await TFChunkHelper.CreateNewChunk(GetFilePathFor("file1"), 2000);
		using (var reader = await chunk.AcquireDataReader())
		{
			chunk.MarkForDeletion();
			var buffer = new byte[1024];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsFalse(result.IsEOF);
			Assert.AreEqual(0, result.BytesRead); // no data yet
		}

		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task a_read_on_new_file_can_be_performed_but_returns_nothing()
	{
		var chunk = await TFChunkHelper.CreateNewChunk(GetFilePathFor("file1"), 2000);
		using (var reader = await chunk.AcquireDataReader())
		{
			var buffer = new byte[1024];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsFalse(result.IsEOF);
			Assert.AreEqual(0, result.BytesRead);
		}

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task a_read_past_end_of_completed_chunk_does_not_include_footer()
	{
		var chunk = await TFChunkHelper.CreateNewChunk(GetFilePathFor("file1"), 300);
		await chunk.Complete(CancellationToken.None); // chunk has 0 bytes of actual data
		using (var reader = await chunk.AcquireDataReader())
		{
			var buffer = new byte[1024];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsTrue(result.IsEOF);
			Assert.AreEqual(0, result.BytesRead);
		}

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task a_read_on_scavenged_chunk_does_not_include_map()
	{
		var chunk = await TFChunkHelper.CreateNewChunk(GetFilePathFor("afile"), 200, isScavenged: true);
		await chunk.CompleteScavenge([new PosMap(0, 0), new PosMap(1, 1)], CancellationToken.None);
		using (var reader = await chunk.AcquireDataReader())
		{
			var buffer = new byte[1024];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsTrue(result.IsEOF);
			Assert.AreEqual(0, result.BytesRead); //header 128 + footer 128 + map 16
		}

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task if_asked_for_more_than_buffer_size_will_only_read_buffer_size()
	{
		var chunk = await TFChunkHelper.CreateNewChunk(GetFilePathFor("file1"), 3000);
		var recordFactory = LogFormatHelper<TLogFormat, TStreamId>.RecordFactory;
		var streamId = LogFormatHelper<TLogFormat, TStreamId>.StreamId;
		var eventTypeId = LogFormatHelper<TLogFormat, TStreamId>.EventTypeId;
		var rec = LogRecord.Prepare(recordFactory, 0, Guid.NewGuid(),
			Guid.NewGuid(), 0, 0, streamId, -1, PrepareFlags.None, eventTypeId,
			new byte[2000], null);
		Assert.IsTrue((await chunk.TryAppend(rec, CancellationToken.None)).Success, "Record was not appended");

		using (var reader = await chunk.AcquireDataReader())
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
	public async Task a_read_past_eof_doesnt_return_eof_if_chunk_is_not_yet_completed()
	{
		var chunk = await TFChunkHelper.CreateNewChunk(GetFilePathFor("file1"), 300);
		var rec = LogRecord.Commit(0, Guid.NewGuid(), 0, 0);
		Assert.IsTrue((await chunk.TryAppend(rec, CancellationToken.None)).Success, "Record was not appended");
		using (var reader = await chunk.AcquireDataReader())
		{
			var buffer = new byte[1024];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsFalse(result.IsEOF, "EOF was returned.");
			//does not include header and footer space
			Assert.AreEqual(rec.GetSizeWithLengthPrefixAndSuffix(), result.BytesRead,
				"Read wrong number of bytes.");
		}

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task a_read_past_eof_returns_eof_if_chunk_is_completed()
	{
		var chunk = await TFChunkHelper.CreateNewChunk(GetFilePathFor("file1"), 300);

		var rec = LogRecord.Commit(0, Guid.NewGuid(), 0, 0);
		Assert.IsTrue((await chunk.TryAppend(rec, CancellationToken.None)).Success, "Record was not appended");
		await chunk.Complete(CancellationToken.None);

		using (var reader = await chunk.AcquireDataReader())
		{
			var buffer = new byte[1024];
			var result = await reader.ReadNextBytes(buffer, CancellationToken.None);
			Assert.IsTrue(result.IsEOF, "EOF was not returned.");
			//does not include header and footer space
			Assert.AreEqual(rec.GetSizeWithLengthPrefixAndSuffix(), result.BytesRead,
				"Read wrong number of bytes.");
		}

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}

	[Test]
	public async Task a_failed_data_reader_wrap_disposes_the_owned_stream()
	{
		var fileSystem = new TrackingChunkFileSystem(
			new ChunkLocalFileSystem(new VersionedPatternFileNamingStrategy(PathName, "chunk-")));
		var chunk = await TFChunk.CreateNew(
			fileSystem: fileSystem,
			filename: GetFilePathFor("file1"),
			chunkDataSize: 300,
			chunkStartNumber: 0,
			chunkEndNumber: 0,
			isScavenged: false,
			inMem: false,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			asyncIO: false,
			tracker: new TFChunkTracker.NoOp(),
			transformFactory: new IdentityChunkTransformFactory(),
			token: CancellationToken.None);
		await chunk.Complete(CancellationToken.None);
		chunk.UnCacheFromMemory();
		typeof(TFChunk)
			.GetField("_transform", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(chunk, new ThrowingReadChunkTransform());

		var exception = Assert.ThrowsAsync<InvalidOperationException>(async () => await chunk.AcquireDataReader());
		Assert.That(exception!.Message, Is.EqualTo("boom"));
		Assert.That(fileSystem.LastHandle, Is.Not.Null);
		Assert.That(fileSystem.LastHandle!.IsDisposed, Is.True);

		chunk.MarkForDeletion();
		chunk.WaitForDestroy(5000);
	}

	private sealed class ThrowingReadChunkTransform : IChunkTransform
	{
		public IChunkReadTransform Read { get; } = new ThrowingReadChunkReadTransform();
		public IChunkWriteTransform Write { get; } = new IdentityChunkWriteTransform();
	}

	private sealed class ThrowingReadChunkReadTransform : IChunkReadTransform
	{
		public ChunkDataReadStream TransformData(ChunkDataReadStream stream) =>
			throw new InvalidOperationException("boom");
	}

	private sealed class TrackingChunkFileSystem(IChunkFileSystem inner) : IChunkFileSystem
	{
		public TrackingChunkHandle LastHandle { get; private set; }

		public IVersionedFileNamingStrategy NamingStrategy => inner.NamingStrategy;

		public async ValueTask<IChunkHandle> OpenForReadAsync(string fileName, ReadOptimizationHint readOptimizationHint,
			bool asyncIO, CancellationToken token)
		{
			LastHandle = new TrackingChunkHandle(await inner.OpenForReadAsync(fileName, readOptimizationHint, asyncIO, token));
			return LastHandle;
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

	private sealed class TrackingChunkHandle(IChunkHandle inner) : IChunkHandle
	{
		public bool IsDisposed { get; private set; }

		public void Flush() => inner.Flush();

		public ValueTask WriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken token) =>
			inner.WriteAsync(data, offset, token);

		public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken token) =>
			inner.ReadAsync(buffer, offset, token);

		public long Length
		{
			get => inner.Length;
			set => inner.Length = value;
		}

		public string Name => inner.Name;
		public FileAccess Access => inner.Access;

		public ValueTask SetReadOnlyAsync(bool value, CancellationToken token) =>
			inner.SetReadOnlyAsync(value, token);

		public void Dispose()
		{
			if (IsDisposed)
				return;

			IsDisposed = true;
			inner.Dispose();
		}
	}
}
