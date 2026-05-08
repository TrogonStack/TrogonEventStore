using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Core.Transforms;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class WhenOpeningExistingOngoingTfchunk : SpecificationWithFilePerTestFixture
{
	private TFChunk _chunk;
	private TFChunk _testChunk;
	private long _nextLogPosition;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_chunk = await TFChunkHelper.CreateNewChunk(Filename, chunkSize: 10 * 1024);
		var seedResult = await _chunk.TryAppend(
			new CommitLogRecord(0, Guid.NewGuid(), 0, DateTime.UtcNow, 0),
			CancellationToken.None);
		Assert.IsTrue(seedResult.Success);
		Assert.Greater(seedResult.NewPosition, 0);
		_nextLogPosition = seedResult.NewPosition;
		_chunk.TryClose();
		_testChunk = await TFChunk.FromOngoingFile(
			fileSystem: TFChunkHelper.CreateLocalFileSystem(Filename),
			filename: Filename,
			writePosition: (int)_nextLogPosition,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			tracker: new TFChunkTracker.NoOp(),
			asyncIO: false,
			getTransformFactory: DbTransformManager.Default.GetFactoryForExistingChunk,
			token: CancellationToken.None);
	}

	[OneTimeTearDown]
	public override void TestFixtureTearDown()
	{
		_chunk.Dispose();
		_testChunk.Dispose();
		base.TestFixtureTearDown();
	}

	[Test]
	public void the_chunk_is_cached()
	{
		Assert.IsTrue(_testChunk.IsCached);
	}

	[Test]
	public void the_chunk_is_not_readonly()
	{
		Assert.IsFalse(_testChunk.IsReadOnly);
	}

	[Test]
	public async Task can_flush_and_then_write()
	{
		await _testChunk.Flush(CancellationToken.None);

		var result = await _testChunk.TryAppend(
			new CommitLogRecord(_nextLogPosition, Guid.NewGuid(), 0, DateTime.UtcNow, 0),
			CancellationToken.None);

		Assert.IsTrue(result.Success);
	}
}
