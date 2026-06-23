using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.Tests.TransactionLog;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.Transforms;
using EventStore.Core.Transforms.Identity;
using EventStore.Plugins.Transforms;
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
	private readonly CountingArchiveStorage _archiveStorage;
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
		_archiveStorage = new(new LocalArchiveStorage(_archivePath, _archiveChunkNamer, ArchiveCheckpointFile));

		_sut = CreateManager(maxChunksCacheSize: 0);
	}

	private TFChunkManager CreateManager(long maxChunksCacheSize)
	{
		var config = new TFChunkDbConfig(
			path: _dbPath,
			chunkFileSystem: new FileSystemWithArchive(
				ChunkSize,
				_locatorCodec,
				_localFileSystem,
				_archiveStorage),
			chunkSize: ChunkSize,
			maxChunksCacheSize: maxChunksCacheSize,
			writerCheckpoint: new InMemoryCheckpoint(0),
			chaserCheckpoint: new InMemoryCheckpoint(0),
			epochCheckpoint: new InMemoryCheckpoint(-1),
			proposalCheckpoint: new InMemoryCheckpoint(-1),
			truncateCheckpoint: new InMemoryCheckpoint(-1),
			replicationCheckpoint: new InMemoryCheckpoint(-1),
			indexCheckpoint: new InMemoryCheckpoint(-1),
			streamExistenceFilterCheckpoint: new InMemoryCheckpoint(-1));

		return new TFChunkManager(config, new TFChunkTracker.NoOp(), DbTransformManager.Default);
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
	public async Task reads_only_archive_header_when_switching_in_archived_chunks()
	{
		var chunk0 = await AddLocalChunk(0, 0);
		var chunk2 = await AddLocalChunk(2, 2);
		await StoreArchivedChunk(1);
		_archiveStorage.ResetCounts();

		var switched = await _sut.SwitchInCompletedChunks([
			_locatorCodec.EncodeRemote(1),
		], CancellationToken.None);

		Assert.True(switched);
		Assert.Equal(0, _archiveStorage.GetChunkLengthCalls);
		Assert.Equal(0, _archiveStorage.GetChunkCalls);
		Assert.Equal(1, _archiveStorage.GetChunkRangeCalls);
		Assert.Equal(new[]
		{
			chunk0.ChunkLocator,
			_locatorCodec.EncodeRemote(1),
			chunk2.ChunkLocator,
		}, ActualChunkInfoLocators);
	}

	[Fact]
	public async Task uses_archived_header_range_when_switching_in_archived_chunks()
	{
		var chunk0 = await AddLocalChunk(0, 0);
		var chunk4 = await AddLocalChunk(4, 4);
		await AddLocalChunk(1, 3);
		await StoreArchivedChunk(1, 3, destinationChunkNumber: 1);

		var switched = await _sut.SwitchInCompletedChunks([
			_locatorCodec.EncodeRemote(1),
		], CancellationToken.None);

		Assert.True(switched);
		Assert.Equal(new[]
		{
			chunk0.ChunkLocator,
			_locatorCodec.EncodeRemote(1),
			_locatorCodec.EncodeRemote(1),
			_locatorCodec.EncodeRemote(1),
			chunk4.ChunkLocator,
		}, ActualChunkInfoLocators);
		Assert.Equal(1, _sut.GetChunkInfo(1).ChunkStartNumber);
		Assert.Equal(3, _sut.GetChunkInfo(1).ChunkEndNumber);
	}

	[Fact]
	public async Task reserves_cache_budget_for_uninitialized_archive_chunks()
	{
		var manager = CreateManager(maxChunksCacheSize: ChunkSize - 1);
		try
		{
			var olderChunk = await AddLocalChunk(manager, 0, 0);
			await AddLocalChunk(manager, 1, 1);
			var latestChunk = await AddLocalChunk(manager, 2, 2);
			await StoreArchivedChunk(1);

			var switched = await manager.SwitchInCompletedChunks([
				_locatorCodec.EncodeRemote(1),
			], CancellationToken.None);

			await RunCachePass(manager);

			Assert.True(switched);
			Assert.False(olderChunk.IsCached);
			Assert.True(latestChunk.IsCached);
		}
		finally
		{
			await manager.TryClose(CancellationToken.None);
		}
	}

	[Fact]
	public async Task initializes_archived_chunk_on_first_access()
	{
		await AddLocalChunk(0, 0);
		await StoreArchivedChunk(1);
		await AddLocalChunk(2, 2);
		_archiveStorage.ResetCounts();
		await _sut.SwitchInCompletedChunks([
			_locatorCodec.EncodeRemote(1),
		], CancellationToken.None);
		_archiveStorage.ResetCounts();

		var chunk = _sut.GetChunk(1);

		Assert.True(chunk.IsRemote);
		Assert.Equal(1, chunk.ChunkHeader.ChunkStartNumber);
		Assert.Equal(1, chunk.ChunkHeader.ChunkEndNumber);
		Assert.True(_archiveStorage.GetChunkLengthCalls > 0);
		Assert.True(_archiveStorage.GetChunkRangeCalls > 0);
	}

	[Fact]
	public async Task try_get_chunk_for_propagates_archive_initialization_failures()
	{
		await AddLocalChunk(0, 0);
		await StoreArchivedChunk(1);
		await AddLocalChunk(2, 2);
		await _sut.SwitchInCompletedChunks([
			_locatorCodec.EncodeRemote(1),
		], CancellationToken.None);
		_archiveStorage.GetChunkLengthException = new IOException("archive chunk is unavailable");

		var ex = Assert.Throws<IOException>(() =>
		{
			_sut.TryGetChunkFor(ChunkSize, out _);
		});

		Assert.Equal("archive chunk is unavailable", ex.Message);
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

	[Fact]
	public async Task switch_chunk_throws_when_the_replacement_range_is_rejected()
	{
		var chunk0 = await AddLocalChunk(0, 0);
		var chunk13 = await AddLocalChunk(1, 3);
		var chunk45 = await AddLocalChunk(4, 5);
		using var newChunk = await CreateCompletedTempChunk(2, 3);

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
			await _sut.SwitchChunk(
				newChunk,
				verifyHash: false,
				removeChunksWithGreaterNumbers: false,
				CancellationToken.None));

		Assert.Equal("Failed to switch in chunk #2-3.", ex.Message);
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

	private string[] ActualChunkInfoLocators =>
		Enumerable.Range(0, _sut.ChunksCount)
			.Select(chunkNum => _sut.GetChunkInfo(chunkNum).ChunkLocator)
			.ToArray();

	private static async ValueTask RunCachePass(TFChunkManager manager)
	{
		var cachePass = typeof(TFChunkManager).GetMethod(
			"CacheUncacheReadOnlyChunks",
			BindingFlags.Instance | BindingFlags.NonPublic);

		await (ValueTask)cachePass!.Invoke(manager, [CancellationToken.None])!;
	}

	private ValueTask<TFChunk> AddLocalChunk(int start, int end) =>
		AddLocalChunk(_sut, start, end);

	private async ValueTask<TFChunk> AddLocalChunk(TFChunkManager manager, int start, int end)
	{
		var fileName = _localFileSystem.NamingStrategy.GetFilenameFor(start, 0);
		var chunk = await TFChunk.CreateNew(
			fileSystem: _localFileSystem,
			filename: fileName,
			chunkDataSize: ChunkSize,
			chunkStartNumber: start,
			chunkEndNumber: end,
			isScavenged: true,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			asyncIO: false,
			tracker: new TFChunkTracker.NoOp(),
			transformFactory: new IdentityChunkTransformFactory(),
			token: CancellationToken.None);
		await chunk.CompleteScavenge([], CancellationToken.None);
		await manager.AddChunk(chunk, CancellationToken.None);
		return chunk;
	}

	private async ValueTask<TFChunk> CreateCompletedTempChunk(int start, int end)
	{
		var chunk = await _sut.CreateTempChunk(
			new ChunkHeader(
				version: (byte)TFChunk.ChunkVersions.Transformed,
				minCompatibleVersion: (byte)TFChunk.ChunkVersions.Transformed,
				chunkSize: ChunkSize,
				chunkStartNumber: start,
				chunkEndNumber: end,
				isScavenged: true,
				chunkId: Guid.NewGuid(),
				transformType: TransformType.Identity),
			fileSize: ChunkSize,
			CancellationToken.None);
		await chunk.CompleteScavenge([], CancellationToken.None);
		return chunk;
	}

	private ValueTask StoreArchivedChunk(int chunkNumber) =>
		StoreArchivedChunk(chunkNumber, chunkNumber, destinationChunkNumber: chunkNumber);

	private async ValueTask StoreArchivedChunk(int chunkStartNumber, int chunkEndNumber, int destinationChunkNumber)
	{
		var sourcePath = Path.Combine(_dbPath, $"archive-source-{chunkStartNumber}-{chunkEndNumber}.tmp");
		var sourceChunk = await TFChunk.CreateNew(
			fileSystem: _localFileSystem,
			filename: sourcePath,
			chunkDataSize: ChunkSize,
			chunkStartNumber: chunkStartNumber,
			chunkEndNumber: chunkEndNumber,
			isScavenged: true,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			asyncIO: false,
			tracker: new TFChunkTracker.NoOp(),
			transformFactory: new IdentityChunkTransformFactory(),
			token: CancellationToken.None);
		await sourceChunk.CompleteScavenge([], CancellationToken.None);
		sourceChunk.Dispose();

		await _archiveStorage.StoreChunk(
			sourcePath,
			_archiveChunkNamer.GetFileNameFor(destinationChunkNumber),
			CancellationToken.None);
	}

	private sealed class CountingArchiveStorage(LocalArchiveStorage inner) : IArchiveStorageReader, IArchiveStorageWriter
	{
		public int GetChunkLengthCalls { get; private set; }

		public int GetChunkCalls { get; private set; }

		public int GetChunkRangeCalls { get; private set; }

		public Exception GetChunkLengthException { get; set; }

		public IArchiveChunkNamer ChunkNamer => inner.ChunkNamer;

		public void ResetCounts()
		{
			GetChunkLengthCalls = 0;
			GetChunkCalls = 0;
			GetChunkRangeCalls = 0;
		}

		public ValueTask<long> GetCheckpoint(CancellationToken ct) =>
			inner.GetCheckpoint(ct);

		public ValueTask<long> GetChunkLength(string chunkFile, CancellationToken ct)
		{
			GetChunkLengthCalls++;
			if (GetChunkLengthException is not null)
			{
				throw GetChunkLengthException;
			}

			return inner.GetChunkLength(chunkFile, ct);
		}

		public ValueTask<Stream> GetChunk(string chunkFile, CancellationToken ct)
		{
			GetChunkCalls++;
			return inner.GetChunk(chunkFile, ct);
		}

		public ValueTask<Stream> GetChunk(string chunkFile, long start, long end, CancellationToken ct)
		{
			GetChunkRangeCalls++;
			return inner.GetChunk(chunkFile, start, end, ct);
		}

		public IAsyncEnumerable<string> ListChunks(CancellationToken ct) =>
			inner.ListChunks(ct);

		public ValueTask<bool> StoreChunk(string chunkPath, string destinationFile, CancellationToken ct) =>
			inner.StoreChunk(chunkPath, destinationFile, ct);

		public ValueTask<bool> SetCheckpoint(long checkpoint, CancellationToken ct) =>
			inner.SetCheckpoint(checkpoint, ct);
	}
}
