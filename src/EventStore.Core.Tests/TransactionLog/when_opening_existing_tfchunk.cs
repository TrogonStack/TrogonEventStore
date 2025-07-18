using System;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Core.Transforms.Identity;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class WhenOpeningExistingTfchunk : SpecificationWithFilePerTestFixture
{
	private TFChunk _chunk;
	private TFChunk _testChunk;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_chunk = await TFChunkHelper.CreateNewChunk(Filename);
		_chunk.Complete();
		_testChunk = await TFChunk.FromCompletedFile(Filename, true, false,
			reduceFileCachePressure: false, tracker: new TFChunkTracker.NoOp(),
			getTransformFactory: _ => new IdentityChunkTransformFactory());
	}

	[TearDown]
	public override void TestFixtureTearDown()
	{
		_chunk.Dispose();
		_testChunk.Dispose();
		base.TestFixtureTearDown();
	}

	[Test]
	public void the_chunk_is_not_cached()
	{
		Assert.IsFalse(_testChunk.IsCached);
	}

	[Test]
	public void the_chunk_is_readonly()
	{
		Assert.IsTrue(_testChunk.IsReadOnly);
	}

	[Test]
	public void append_throws_invalid_operation_exception()
	{
		Assert.Throws<InvalidOperationException>(() =>
			_testChunk.TryAppend(new CommitLogRecord(0, Guid.NewGuid(), 0, DateTime.UtcNow, 0)));
	}

	[Test]
	public void flush_does_not_throw_any_exception()
	{
		Assert.DoesNotThrow(() => _testChunk.Flush());
	}
}
