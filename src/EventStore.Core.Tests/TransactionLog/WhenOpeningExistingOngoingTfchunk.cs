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

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_chunk = await TFChunkHelper.CreateNewChunk(Filename, chunkSize: 10 * 1024);
		_chunk.TryClose();
		_testChunk = await TFChunk.FromOngoingFile(
			fileSystem: TFChunkHelper.CreateLocalFileSystem(Filename),
			filename: Filename,
			writePosition: 0,
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
			new CommitLogRecord(0, Guid.NewGuid(), 0, DateTime.UtcNow, 0),
			CancellationToken.None);

		Assert.IsTrue(result.Success);
	}
}
