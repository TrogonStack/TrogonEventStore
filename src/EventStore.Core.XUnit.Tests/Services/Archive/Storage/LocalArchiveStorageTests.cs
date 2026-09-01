using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Tests.TransactionLog;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Storage;

public class LocalArchiveStorageTests : DirectoryPerTest<LocalArchiveStorageTests>
{
	private const string ArchiveCheckpointFile = "archive.chk";
	private const int CheckpointUpdates = 2_000;

	[Fact]
	public async Task checkpoint_can_be_replaced_while_a_reader_is_open()
	{
		var archive = new LocalArchiveStorage(
			Fixture.Directory,
			new ArchiveChunkNamer(new VersionedPatternFileNamingStrategy(Fixture.Directory, "chunk-")),
			ArchiveCheckpointFile);
		await archive.SetCheckpoint(1, CancellationToken.None);

		using var handle = File.OpenHandle(
			Path.Combine(Fixture.Directory, ArchiveCheckpointFile),
			share: FileShare.ReadWrite | FileShare.Delete);
		await archive.SetCheckpoint(2, CancellationToken.None);

		Assert.Equal(2, await archive.GetCheckpoint(CancellationToken.None));
	}

	[Fact]
	public async Task concurrent_checkpoint_reads_never_observe_partial_updates()
	{
		var archive = new LocalArchiveStorage(
			Fixture.Directory,
			new ArchiveChunkNamer(new VersionedPatternFileNamingStrategy(Fixture.Directory, "chunk-")),
			ArchiveCheckpointFile);
		await archive.SetCheckpoint(0, CancellationToken.None);

		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstCheckpointPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var checkpointObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var writerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var checkpointReads = 0;
		var writer = Task.Run(async () =>
		{
			await start.Task;
			try
			{
				for (var checkpoint = 1; checkpoint <= CheckpointUpdates; checkpoint++)
				{
					await archive.SetCheckpoint(checkpoint, CancellationToken.None);
					if (checkpoint == 1)
					{
						firstCheckpointPublished.SetResult();
						await checkpointObserved.Task;
					}
					await Task.Yield();
				}
			}
			finally
			{
				writerCompleted.SetResult();
			}
		});

		var readers = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount, 2, 8))
			.Select(_ => Task.Run(async () =>
			{
				await start.Task;
				await firstCheckpointPublished.Task;
				while (!writerCompleted.Task.IsCompleted)
				{
					Assert.InRange(await archive.GetCheckpoint(CancellationToken.None), 0, CheckpointUpdates);
					Interlocked.Increment(ref checkpointReads);
					checkpointObserved.TrySetResult();
					await Task.Yield();
				}
			}))
			.ToArray();

		start.SetResult();
		await Task.WhenAll(readers.Append(writer));
		Assert.True(checkpointReads > 0);
	}
}
