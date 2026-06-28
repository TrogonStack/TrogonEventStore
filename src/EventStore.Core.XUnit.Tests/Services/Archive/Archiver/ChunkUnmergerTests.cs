using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive.Archiver.Unmerger;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Core.Transforms;
using EventStore.Core.Transforms.Identity;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Archiver;

public sealed class ChunkUnmergerTests : DirectoryPerTest<ChunkUnmergerTests>
{
	private const int ChunkSize = 4096;

	private readonly string _dbPath;
	private readonly ChunkLocalFileSystem _fileSystem;
	private readonly TFChunkDbConfig _dbConfig;
	private readonly ChunkUnmerger _sut;

	public ChunkUnmergerTests()
	{
		_dbPath = Path.Combine(Fixture.Directory, "db");
		Directory.CreateDirectory(_dbPath);

		var namingStrategy = new VersionedPatternFileNamingStrategy(_dbPath, "chunk-");
		_fileSystem = new ChunkLocalFileSystem(namingStrategy);
		_dbConfig = new TFChunkDbConfig(
			_dbPath,
			_fileSystem,
			ChunkSize,
			maxChunksCacheSize: 0,
			writerCheckpoint: new InMemoryCheckpoint(0),
			chaserCheckpoint: new InMemoryCheckpoint(0),
			epochCheckpoint: new InMemoryCheckpoint(-1),
			proposalCheckpoint: new InMemoryCheckpoint(-1),
			truncateCheckpoint: new InMemoryCheckpoint(-1),
			replicationCheckpoint: new InMemoryCheckpoint(-1),
			indexCheckpoint: new InMemoryCheckpoint(-1),
			streamExistenceFilterCheckpoint: new InMemoryCheckpoint(-1));
		_sut = new ChunkUnmerger(_dbConfig, DbTransformManager.Default);
	}

	[Fact]
	public async Task creates_one_completed_chunk_per_logical_chunk()
	{
		var sourceChunk = await CreateMergedChunk(
			CreatePrepare(logPosition: 0, "stream-a"),
			CreatePrepare(logPosition: ChunkSize, "stream-b"));

		var files = await _sut.Unmerge(sourceChunk, 0, 1).ToArrayAsync();

		Assert.Equal(2, files.Length);
		Assert.All(files, file => Assert.True(File.Exists(file)));

		using var firstChunk = await OpenCompletedChunk(files[0]);
		using var secondChunk = await OpenCompletedChunk(files[1]);

		AssertChunkRange(firstChunk, 0);
		AssertChunkRange(secondChunk, 1);

		Assert.Equal("stream-a", await ReadStreamId(firstChunk));
		Assert.Equal("stream-b", await ReadStreamId(secondChunk));
	}

	[Fact]
	public async Task creates_empty_chunks_for_empty_logical_ranges()
	{
		var sourceChunk = await CreateMergedChunk(
			CreatePrepare(logPosition: 0, "stream-a"));

		var files = await _sut.Unmerge(sourceChunk, 0, 1).ToArrayAsync();

		Assert.Equal(2, files.Length);

		using var firstChunk = await OpenCompletedChunk(files[0]);
		using var secondChunk = await OpenCompletedChunk(files[1]);

		Assert.Equal("stream-a", await ReadStreamId(firstChunk));
		Assert.False((await secondChunk.TryReadFirst(CancellationToken.None)).Success);
	}

	[Fact]
	public async Task rejects_chunks_that_do_not_match_the_requested_range()
	{
		var sourceChunk = await CreateMergedChunk(
			CreatePrepare(logPosition: 0, "stream-a"));

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
			await _sut.Unmerge(sourceChunk, 0, 2).ToArrayAsync());

		Assert.Contains("does not match requested range #0-2", ex.Message);
	}

	[Fact]
	public async Task removes_temp_files_when_enumeration_stops_before_completion()
	{
		var sourceChunk = await CreateMergedChunk(
			CreatePrepare(logPosition: 0, "stream-a"),
			CreatePrepare(logPosition: ChunkSize, "stream-b"));

		var enumerator = _sut.Unmerge(sourceChunk, 0, 1).GetAsyncEnumerator();
		Assert.True(await enumerator.MoveNextAsync());
		Assert.NotEmpty(_fileSystem.NamingStrategy.GetAllTempFiles());

		await enumerator.DisposeAsync();

		Assert.Empty(_fileSystem.NamingStrategy.GetAllTempFiles());
	}

	private async ValueTask<string> CreateMergedChunk(params PrepareLogRecord[] records)
	{
		var chunkPath = _fileSystem.NamingStrategy.GetFilenameFor(0, 0);
		using var chunk = await TFChunk.CreateNew(
			_fileSystem,
			chunkPath,
			ChunkSize,
			chunkStartNumber: 0,
			chunkEndNumber: 1,
			isScavenged: true,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			asyncIO: false,
			tracker: new TFChunkTracker.NoOp(),
			transformFactory: new IdentityChunkTransformFactory(),
			token: CancellationToken.None);

		var positionMap = new List<PosMap>(records.Length);
		foreach (var record in records)
		{
			positionMap.Add(await TFChunkScavenger<string>.WriteRecord(chunk, record, CancellationToken.None));
		}

		await chunk.CompleteScavenge(positionMap, CancellationToken.None);
		return chunkPath;
	}

	private ValueTask<TFChunk> OpenCompletedChunk(string chunkPath) =>
		TFChunk.FromCompletedFile(
			_fileSystem,
			chunkPath,
			verifyHash: true,
			unbufferedRead: false,
			tracker: new TFChunkTracker.NoOp(),
			getTransformFactory: DbTransformManager.Default.GetFactoryForExistingChunk,
			token: CancellationToken.None);

	private static void AssertChunkRange(TFChunk chunk, int chunkNumber)
	{
		Assert.True(chunk.IsReadOnly);
		Assert.True(chunk.ChunkHeader.IsScavenged);
		Assert.Equal(chunkNumber, chunk.ChunkHeader.ChunkStartNumber);
		Assert.Equal(chunkNumber, chunk.ChunkHeader.ChunkEndNumber);
	}

	private static async ValueTask<string> ReadStreamId(TFChunk chunk)
	{
		var result = await chunk.TryReadFirst(CancellationToken.None);
		Assert.True(result.Success);
		return Assert.IsType<PrepareLogRecord>(result.LogRecord).EventStreamId;
	}

	private static PrepareLogRecord CreatePrepare(long logPosition, string streamId) =>
		new(
			logPosition,
			Guid.NewGuid(),
			Guid.NewGuid(),
			transactionPosition: logPosition,
			transactionOffset: 0,
			streamId,
			eventStreamIdSize: null,
			expectedVersion: 0,
			DateTime.UtcNow,
			PrepareFlags.SingleWrite,
			eventType: "event-type",
			eventTypeSize: null,
			Array.Empty<byte>(),
			Array.Empty<byte>());
}
