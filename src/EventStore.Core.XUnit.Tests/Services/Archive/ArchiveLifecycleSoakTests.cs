using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Archive.Archiver;
using EventStore.Core.Services.Archive.Archiver.Unmerger;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.Tests.TransactionLog;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.Transforms.Identity;
using EventStore.Plugins.Transforms;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive;

using ArchiveCatchupService = Core.Services.Archive.ArchiveCatchup.ArchiveCatchup;

public class ArchiveLifecycleSoakTests : DirectoryPerTest<ArchiveLifecycleSoakTests>
{
	private const int ChunkCount = 8;
	private const int ChunkSize = 4096;
	private const string ArchiveCheckpointFile = "archive.chk";

	[Fact]
	public async Task archives_removes_reads_and_recovers_multiple_nodes_across_restart()
	{
		var leaderPath = CreateDirectory("leader");
		var archivePath = CreateDirectory("archive");
		var followerPaths = new[] { CreateDirectory("follower-1"), CreateDirectory("follower-2") };
		var leaderNaming = new VersionedPatternFileNamingStrategy(leaderPath, "chunk-");
		var archiveNamer = new ArchiveChunkNamer(
			new VersionedPatternFileNamingStrategy(archivePath, "chunk-"));
		var archive = new LocalArchiveStorage(archivePath, archiveNamer, ArchiveCheckpointFile);
		var archiveFactory = new ArchiveStorageFactoryAdapter(archive);
		var chunks = await CreateCompletedChunks(leaderNaming);
		var archiver = new ArchiverService(new NoOpSubscriber(), archiveFactory, new UnexpectedUnmerger(), archiveNamer);

		foreach (var chunk in chunks)
		{
			archiver.Handle(new SystemMessage.ChunkLoaded(chunk));
		}

		archiver.Handle(new ReplicationTrackingMessage.ReplicatedTo(chunks[^1].ChunkEndPosition));
		await WaitForCheckpoint(archive, chunks[^1].ChunkEndPosition);

		Assert.Equal(
			Enumerable.Range(0, ChunkCount).Select(archiveNamer.GetFileNameFor),
			await archive.ListChunks(CancellationToken.None).ToArrayAsync());

		foreach (var chunk in chunks)
		{
			File.Delete(chunk.ChunkLocator);
		}

		Assert.Empty(Directory.EnumerateFiles(leaderPath, "chunk-*"));
		await VerifyColdReads(leaderPath, archive, chunks);

		await Task.WhenAll(followerPaths.Select(path => CatchUpNode(path, archiveFactory)));
		foreach (var followerPath in followerPaths)
		{
			await VerifyRestart(followerPath, archiveFactory, chunks[^1].ChunkEndPosition);
			await VerifyLocalChunks(followerPath);
		}

		archiver.Handle(new SystemMessage.BecomeShuttingDown(Guid.NewGuid(), exitProcess: true, shutdownHttp: true));
	}

	private string CreateDirectory(string name)
	{
		var path = Path.Combine(Fixture.Directory, name);
		Directory.CreateDirectory(path);
		return path;
	}

	private static async Task<ChunkInfo[]> CreateCompletedChunks(IVersionedFileNamingStrategy namingStrategy)
	{
		var fileSystem = new ChunkLocalFileSystem(namingStrategy);
		var chunks = new List<ChunkInfo>(ChunkCount);
		for (var chunkNumber = 0; chunkNumber < ChunkCount; chunkNumber++)
		{
			var chunk = await TFChunk.CreateNew(
				fileSystem,
				namingStrategy.GetFilenameFor(chunkNumber, 0),
				ChunkSize,
				chunkNumber,
				chunkNumber,
				isScavenged: true,
				unbuffered: false,
				writethrough: false,
				reduceFileCachePressure: false,
				asyncIO: false,
				new TFChunkTracker.NoOp(),
				new IdentityChunkTransformFactory(),
				CancellationToken.None);
			await chunk.CompleteScavenge([], CancellationToken.None);
			chunks.Add(chunk.ChunkInfo);
			chunk.Dispose();
		}

		return chunks.ToArray();
	}

	private static async Task WaitForCheckpoint(IArchiveStorageReader archive, long expected)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		while (await archive.GetCheckpoint(timeout.Token) != expected)
		{
			await Task.Delay(25, timeout.Token);
		}
	}

	private static async Task VerifyColdReads(
		string leaderPath,
		IArchiveStorageReader archive,
		IReadOnlyList<ChunkInfo> chunks)
	{
		var fileSystem = new FileSystemWithArchive(
			ChunkSize,
			new PrefixingLocatorCodec(),
			new ChunkLocalFileSystem(new VersionedPatternFileNamingStrategy(leaderPath, "chunk-")),
			archive);

		for (var chunkNumber = 0; chunkNumber < chunks.Count; chunkNumber++)
		{
			using var chunk = await TFChunk.FromCompletedFile(
				fileSystem,
				$"archived-chunk-{chunkNumber}",
				verifyHash: false,
				unbufferedRead: false,
				new TFChunkTracker.NoOp(),
				static _ => new IdentityChunkTransformFactory(),
				token: CancellationToken.None);

			Assert.True(chunk.IsRemote);
			Assert.True(chunk.ChunkFooter.IsCompleted);
			Assert.Equal(chunkNumber, chunk.ChunkHeader.ChunkStartNumber);
			Assert.Equal(chunkNumber, chunk.ChunkHeader.ChunkEndNumber);
		}
	}

	private static async Task CatchUpNode(string dbPath, IArchiveStorageFactory archiveFactory)
	{
		using var checkpoints = new NodeCheckpoints(dbPath);
		var catchup = CreateCatchup(dbPath, checkpoints, archiveFactory);
		await catchup.Run();

		Assert.Equal((long)ChunkCount * ChunkSize, checkpoints.Writer.Read());
		Assert.Equal((long)ChunkCount * ChunkSize, checkpoints.Chaser.Read());
		Assert.Equal(-1, checkpoints.Epoch.Read());
	}

	private static async Task VerifyRestart(string dbPath, IArchiveStorageFactory archiveFactory, long expectedCheckpoint)
	{
		using var checkpoints = new NodeCheckpoints(dbPath, mustExist: true);
		Assert.Equal(expectedCheckpoint, checkpoints.Writer.Read());
		await CreateCatchup(dbPath, checkpoints, archiveFactory).Run();
		Assert.Equal(expectedCheckpoint, checkpoints.Writer.Read());
		Assert.Equal(expectedCheckpoint, checkpoints.Chaser.Read());
	}

	private static ArchiveCatchupService CreateCatchup(
		string dbPath,
		NodeCheckpoints checkpoints,
		IArchiveStorageFactory archiveFactory) =>
		new(
			dbPath,
			checkpoints.Writer,
			checkpoints.Chaser,
			checkpoints.Epoch,
			ChunkSize,
			new VersionedPatternFileNamingStrategy(dbPath, "chunk-"),
			archiveFactory);

	private static async Task VerifyLocalChunks(string dbPath)
	{
		var namingStrategy = new VersionedPatternFileNamingStrategy(dbPath, "chunk-");
		var fileSystem = new ChunkLocalFileSystem(namingStrategy);
		for (var chunkNumber = 0; chunkNumber < ChunkCount; chunkNumber++)
		{
			using var chunk = await TFChunk.FromCompletedFile(
				fileSystem,
				namingStrategy.GetFilenameFor(chunkNumber, 1),
				verifyHash: true,
				unbufferedRead: false,
				new TFChunkTracker.NoOp(),
				static _ => new IdentityChunkTransformFactory(),
				token: CancellationToken.None);
			Assert.Equal(chunkNumber, chunk.ChunkHeader.ChunkStartNumber);
		}
	}

	private sealed class NodeCheckpoints : IDisposable
	{
		public FileCheckpoint Writer { get; }
		public FileCheckpoint Chaser { get; }
		public FileCheckpoint Epoch { get; }

		public NodeCheckpoints(string dbPath, bool mustExist = false)
		{
			Writer = new(Path.Combine(dbPath, "writer.chk"), "writer", mustExist);
			Chaser = new(Path.Combine(dbPath, "chaser.chk"), "chaser", mustExist);
			Epoch = new(Path.Combine(dbPath, "epoch.chk"), "epoch", mustExist, initValue: -1);
		}

		public void Dispose()
		{
			Writer.Close(flush: true);
			Chaser.Close(flush: true);
			Epoch.Close(flush: true);
		}
	}

	private sealed class ArchiveStorageFactoryAdapter(LocalArchiveStorage storage) : IArchiveStorageFactory
	{
		public IArchiveStorageReader CreateReader() => storage;
		public IArchiveStorageWriter CreateWriter() => storage;
	}

	private sealed class NoOpSubscriber : ISubscriber
	{
		public void Subscribe<T>(IAsyncHandle<T> handler) where T : Message { }
		public void Unsubscribe<T>(IAsyncHandle<T> handler) where T : Message { }
	}

	private sealed class UnexpectedUnmerger : IChunkUnmerger
	{
		public IAsyncEnumerable<string> Unmerge(string chunkPath, int chunkStartNumber, int chunkEndNumber) =>
			throw new InvalidOperationException("The lifecycle test archives only individual chunks.");
	}
}
