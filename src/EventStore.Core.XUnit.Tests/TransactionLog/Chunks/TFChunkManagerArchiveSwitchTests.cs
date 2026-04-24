using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.Transforms;
using EventStore.Core.Transforms.Identity;
using Xunit;

namespace EventStore.Core.XUnit.Tests.TransactionLog.Chunks;

public class TFChunkManagerArchiveSwitchTests : IAsyncLifetime
{
	private const string ArchiveCheckpointFile = "archive.chk";
	private const int ChunkSize = 4096;

	private readonly DirectoryFixture<TFChunkManagerArchiveSwitchTests> _fixture = new();
	private readonly string _dbPath;
	private readonly string _archivePath;
	private readonly ChunkLocalFileSystem _localFileSystem;
	private readonly ILocatorCodec _locatorCodec = new PrefixingLocatorCodec();
	private readonly FileSystemWriter _archiveWriter;
	private readonly ArchiveChunkNamer _archiveChunkNamer;
	private readonly TFChunkManager _sut;

	public TFChunkManagerArchiveSwitchTests()
	{
		_dbPath = Path.Combine(_fixture.Directory, "db");
		_archivePath = Path.Combine(_fixture.Directory, "archive");
		Directory.CreateDirectory(_dbPath);
		Directory.CreateDirectory(_archivePath);

		var dbNamingStrategy = new VersionedPatternFileNamingStrategy(_dbPath, "chunk-");
		_localFileSystem = new ChunkLocalFileSystem(dbNamingStrategy);
		_archiveChunkNamer = new ArchiveChunkNamer(new VersionedPatternFileNamingStrategy(_archivePath, "chunk-"));
		_archiveWriter = new FileSystemWriter(new FileSystemOptions { Path = _archivePath }, ArchiveCheckpointFile);

		var config = new TFChunkDbConfig(
			path: _dbPath,
			chunkFileSystem: new FileSystemWithArchive(
				ChunkSize,
				_locatorCodec,
				_localFileSystem,
				new FileSystemReader(
					new FileSystemOptions { Path = _archivePath },
					_archiveChunkNamer,
					ArchiveCheckpointFile)),
			chunkSize: ChunkSize,
			maxChunksCacheSize: 0,
			writerCheckpoint: new InMemoryCheckpoint(0),
			chaserCheckpoint: new InMemoryCheckpoint(0),
			epochCheckpoint: new InMemoryCheckpoint(-1),
			proposalCheckpoint: new InMemoryCheckpoint(-1),
			truncateCheckpoint: new InMemoryCheckpoint(-1),
			replicationCheckpoint: new InMemoryCheckpoint(-1),
			indexCheckpoint: new InMemoryCheckpoint(-1),
			streamExistenceFilterCheckpoint: new InMemoryCheckpoint(-1));

		_sut = new TFChunkManager(config, new TFChunkTracker.NoOp(), DbTransformManager.Default);
	}

	public Task InitializeAsync() =>
		_fixture.InitializeAsync();

	public async Task DisposeAsync()
	{
		await _sut.TryClose(CancellationToken.None);
		await _fixture.DisposeAsync();
	}

	[Fact]
	public async Task switches_in_archived_chunks_for_a_precise_range()
	{
		var chunk0 = await AddLocalChunk(0, 0);
		var chunk45 = await AddLocalChunk(4, 5);
		await AddLocalChunk(1, 3);
		await StoreArchivedChunk(1);
		await StoreArchivedChunk(2);
		await StoreArchivedChunk(3);

		var switched = await _sut.SwitchInCompletedChunks([
			_locatorCodec.EncodeRemote(1),
			_locatorCodec.EncodeRemote(2),
			_locatorCodec.EncodeRemote(3),
		], CancellationToken.None);

		Assert.True(switched);
		Assert.Equal(new[]
		{
			chunk0.ChunkLocator,
			_locatorCodec.EncodeRemote(1),
			_locatorCodec.EncodeRemote(2),
			_locatorCodec.EncodeRemote(3),
			chunk45.ChunkLocator,
			chunk45.ChunkLocator,
		}, ActualChunks);
	}

	[Fact]
	public async Task rejects_misaligned_archive_ranges_without_mutating_chunks()
	{
		var chunk0 = await AddLocalChunk(0, 0);
		var chunk13 = await AddLocalChunk(1, 3);
		var chunk45 = await AddLocalChunk(4, 5);
		await StoreArchivedChunk(2);
		await StoreArchivedChunk(3);

		var switched = await _sut.SwitchInCompletedChunks([
			_locatorCodec.EncodeRemote(2),
			_locatorCodec.EncodeRemote(3),
		], CancellationToken.None);

		Assert.False(switched);
		Assert.Equal(new[]
		{
			chunk0.ChunkLocator,
			chunk13.ChunkLocator,
			chunk13.ChunkLocator,
			chunk13.ChunkLocator,
			chunk45.ChunkLocator,
			chunk45.ChunkLocator,
		}, ActualChunks);
	}

	private string[] ActualChunks =>
		Enumerable.Range(0, _sut.ChunksCount)
			.Select(chunkNum => _sut.GetChunk(chunkNum).ChunkLocator)
			.ToArray();

	private async ValueTask<TFChunk> AddLocalChunk(int start, int end)
	{
		var fileName = _localFileSystem.NamingStrategy.GetFilenameFor(start, 0);
		var chunk = await TFChunk.CreateNew(
			fileSystem: _localFileSystem,
			filename: fileName,
			chunkDataSize: ChunkSize,
			chunkStartNumber: start,
			chunkEndNumber: end,
			isScavenged: true,
			inMem: false,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			asyncIO: false,
			tracker: new TFChunkTracker.NoOp(),
			transformFactory: new IdentityChunkTransformFactory(),
			token: CancellationToken.None);
		await chunk.CompleteScavenge([], CancellationToken.None);
		await _sut.AddChunk(chunk, CancellationToken.None);
		return chunk;
	}

	private async ValueTask StoreArchivedChunk(int chunkNumber)
	{
		var sourcePath = Path.Combine(_dbPath, $"archive-source-{chunkNumber}.tmp");
		var sourceChunk = await TFChunk.CreateNew(
			fileSystem: _localFileSystem,
			filename: sourcePath,
			chunkDataSize: ChunkSize,
			chunkStartNumber: chunkNumber,
			chunkEndNumber: chunkNumber,
			isScavenged: true,
			inMem: false,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			asyncIO: false,
			tracker: new TFChunkTracker.NoOp(),
			transformFactory: new IdentityChunkTransformFactory(),
			token: CancellationToken.None);
		await sourceChunk.CompleteScavenge([], CancellationToken.None);
		sourceChunk.Dispose();

		await _archiveWriter.StoreChunk(
			sourcePath,
			_archiveChunkNamer.GetFileNameFor(chunkNumber),
			CancellationToken.None);
	}
}
