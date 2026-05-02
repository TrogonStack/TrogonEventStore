using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.Transforms.Identity;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class with_file_system_with_archive : SpecificationWithDirectory
{
	private const string ChunkPrefix = "chunk-";
	private const string ArchiveCheckpointFile = "archive.chk";
	private const int ChunkSize = 4096;

	private string ArchivePath => Path.Combine(PathName, "archive");
	private string DbPath => Path.Combine(PathName, "db");

	[SetUp]
	public override async Task SetUp()
	{
		await base.SetUp();
		Directory.CreateDirectory(ArchivePath);
		Directory.CreateDirectory(DbPath);
	}

	[Test]
	public async Task opens_remote_chunk_handles_for_archive_locators()
	{
		var sourcePath = Path.Combine(DbPath, "source");
		var content = new byte[256];
		new Random(17).NextBytes(content);
		await File.WriteAllBytesAsync(sourcePath, content);

		var dbNamingStrategy = new VersionedPatternFileNamingStrategy(DbPath, ChunkPrefix);
		var archiveChunkNamer = new ArchiveChunkNamer(dbNamingStrategy);
		var writer = new FileSystemWriter(new FileSystemOptions { Path = ArchivePath }, ArchiveCheckpointFile);
		await writer.StoreChunk(sourcePath, archiveChunkNamer.GetFileNameFor(0), CancellationToken.None);

		var sut = CreateSut();
		using var handle = await sut.OpenForReadAsync("archived-chunk-0", ReadOptimizationHint.SequentialScan,
			asyncIO: false, CancellationToken.None);
		var slice = new byte[32];

		Assert.That(handle.Length, Is.EqualTo(content.Length));
		Assert.That(await handle.ReadAsync(slice, 64, CancellationToken.None), Is.EqualTo(slice.Length));
		Assert.That(slice, Is.EqualTo(content.Skip(64).Take(slice.Length).ToArray()));
	}

	[Test]
	public async Task opens_completed_remote_tfchunks_through_the_archive_file_system()
	{
		var sourcePath = Path.Combine(DbPath, "chunk-000000.000000");
		var sourceChunk = await TFChunkHelper.CreateNewChunk(sourcePath, chunkSize: ChunkSize);
		await sourceChunk.Complete(CancellationToken.None);
		var expectedFileSize = sourceChunk.FileSize;
		sourceChunk.Dispose();

		var writer = new FileSystemWriter(new FileSystemOptions { Path = ArchivePath }, ArchiveCheckpointFile);
		var dbNamingStrategy = new VersionedPatternFileNamingStrategy(DbPath, ChunkPrefix);
		await writer.StoreChunk(sourcePath, new ArchiveChunkNamer(dbNamingStrategy).GetFileNameFor(0),
			CancellationToken.None);

		var sut = CreateSut();
		using var remoteChunk = await TFChunk.FromCompletedFile(
			sut,
			"archived-chunk-0",
			verifyHash: false,
			unbufferedRead: false,
			tracker: new TFChunkTracker.NoOp(),
			getTransformFactory: static _ => new IdentityChunkTransformFactory(),
			token: CancellationToken.None);

		Assert.That(remoteChunk.IsRemote, Is.True);
		Assert.That(remoteChunk.FileSize, Is.EqualTo(expectedFileSize));
		Assert.That(remoteChunk.ChunkHeader.ChunkStartNumber, Is.EqualTo(0));
		Assert.That(remoteChunk.ChunkHeader.ChunkEndNumber, Is.EqualTo(0));
	}

	[Test]
	public async Task reads_remote_chunk_metadata_for_archive_locators()
	{
		var sourcePath = Path.Combine(DbPath, "chunk-000000.000000");
		var sourceChunk = await TFChunkHelper.CreateNewChunk(sourcePath, chunkSize: ChunkSize);
		await sourceChunk.Complete(CancellationToken.None);
		sourceChunk.Dispose();

		var writer = new FileSystemWriter(new FileSystemOptions { Path = ArchivePath }, ArchiveCheckpointFile);
		var dbNamingStrategy = new VersionedPatternFileNamingStrategy(DbPath, ChunkPrefix);
		await writer.StoreChunk(sourcePath, new ArchiveChunkNamer(dbNamingStrategy).GetFileNameFor(0),
			CancellationToken.None);

		var sut = CreateSut();
		var header = await sut.ReadHeaderAsync("archived-chunk-0", CancellationToken.None);
		var footer = await sut.ReadFooterAsync("archived-chunk-0", CancellationToken.None);

		Assert.That(header.ChunkStartNumber, Is.EqualTo(0));
		Assert.That(header.ChunkEndNumber, Is.EqualTo(0));
		Assert.That(footer.IsCompleted, Is.True);
	}

	[Test]
	public async Task enumerator_replaces_missing_local_chunks_that_are_already_in_archive()
	{
		var namingStrategy = new VersionedPatternFileNamingStrategy(DbPath, ChunkPrefix);
		var localChunkEnumerator = new FakeChunkEnumerator(
			new MissingVersion(namingStrategy.GetFilenameFor(0, 0), 0),
			new LatestVersion(namingStrategy.GetFilenameFor(1, 0), 1, 1),
			new MissingVersion(namingStrategy.GetFilenameFor(2, 0), 2));

		var sut = new FileSystemWithArchive(
			chunkSize: 1000,
			locatorCodec: new PrefixingLocatorCodec(),
			localFileSystem: new FakeChunkFileSystem(namingStrategy, localChunkEnumerator),
			archive: new FakeArchiveReader(checkpoint: 2000, namingStrategy));

		var results = new List<TFChunkInfo>();
		await foreach (var chunkInfo in sut.CreateChunkEnumerator().EnumerateChunks(2, CancellationToken.None))
		{
			results.Add(chunkInfo);
		}

		Assert.That(results, Is.EqualTo(new TFChunkInfo[]
		{
			new LatestVersion("archived-chunk-0", 0, 0),
			new LatestVersion(namingStrategy.GetFilenameFor(1, 0), 1, 1),
			new MissingVersion(namingStrategy.GetFilenameFor(2, 0), 2),
		}));
	}

	private FileSystemWithArchive CreateSut()
	{
		var dbNamingStrategy = new VersionedPatternFileNamingStrategy(DbPath, ChunkPrefix);
		var archiveNamingStrategy = new VersionedPatternFileNamingStrategy(ArchivePath, ChunkPrefix);
		return new FileSystemWithArchive(
			ChunkSize,
			new PrefixingLocatorCodec(),
			new ChunkLocalFileSystem(dbNamingStrategy),
			new FileSystemReader(
				new FileSystemOptions { Path = ArchivePath },
				new ArchiveChunkNamer(archiveNamingStrategy),
				ArchiveCheckpointFile));
	}

	private sealed class FakeChunkFileSystem(IVersionedFileNamingStrategy namingStrategy, IChunkEnumerator chunkEnumerator)
		: IChunkFileSystem
	{
		public IVersionedFileNamingStrategy NamingStrategy { get; } = namingStrategy;

		public ValueTask<IChunkHandle> OpenForReadAsync(string fileName, ReadOptimizationHint readOptimizationHint,
			bool asyncIO, CancellationToken token) =>
			throw new NotImplementedException();

		public ValueTask<ChunkHeader> ReadHeaderAsync(string fileName, CancellationToken token) =>
			throw new NotImplementedException();

		public ValueTask<ChunkFooter> ReadFooterAsync(string fileName, CancellationToken token) =>
			throw new NotImplementedException();

		public IChunkEnumerator CreateChunkEnumerator() => chunkEnumerator;

		public void MoveFile(string sourceFileName, string destinationFileName) =>
			throw new NotImplementedException();

		public void DeleteFile(string fileName) =>
			throw new NotImplementedException();

		public void SetAttributes(string fileName, FileAttributes fileAttributes) =>
			throw new NotImplementedException();
	}

	private sealed class FakeChunkEnumerator(params TFChunkInfo[] chunkInfos) : IChunkEnumerator
	{
		public async IAsyncEnumerable<TFChunkInfo> EnumerateChunks(int lastChunkNumber,
			[EnumeratorCancellation] CancellationToken token)
		{
			foreach (var chunkInfo in chunkInfos)
			{
				token.ThrowIfCancellationRequested();
				yield return chunkInfo;
				await Task.Yield();
			}
		}
	}

	private sealed class FakeArchiveReader(long checkpoint, IVersionedFileNamingStrategy namingStrategy)
		: IArchiveStorageReader
	{
		public IArchiveChunkNamer ChunkNamer { get; } = new ArchiveChunkNamer(namingStrategy);

		public ValueTask<long> GetCheckpoint(CancellationToken ct) => ValueTask.FromResult(checkpoint);

		public ValueTask<long> GetChunkLength(string chunkFile, CancellationToken ct) =>
			throw new NotImplementedException();

		public ValueTask<Stream> GetChunk(string chunkFile, long start, long end, CancellationToken ct) =>
			throw new NotImplementedException();

		public ValueTask<Stream> GetChunk(string chunkFile, CancellationToken ct) =>
			throw new NotImplementedException();

		public IAsyncEnumerable<string> ListChunks(CancellationToken ct) =>
			throw new NotImplementedException();
	}
}
