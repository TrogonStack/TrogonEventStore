using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class WhenCreatingTfchunkFromEmptyFile : SpecificationWithFile
{
	private TFChunk _chunk;

	[SetUp]
	public override async Task SetUp()
	{
		await base.SetUp();
		_chunk = await TFChunkHelper.CreateNewChunk(Filename, 1024);
	}

	[TearDown]
	public override void TearDown()
	{
		_chunk.Dispose();
		base.TearDown();
	}

	[Test]
	public void the_chunk_is_cached()
	{
		Assert.IsTrue(_chunk.IsCached);
	}

	[Test]
	public void the_file_is_created()
	{
		Assert.IsTrue(File.Exists(Filename));
	}

	[Test]
	public void the_chunk_is_not_readonly()
	{
		Assert.IsFalse(_chunk.IsReadOnly);
	}

	[Test]
	public void append_does_not_throw_exception()
	{
		Assert.DoesNotThrow(() => _chunk.TryAppend(new CommitLogRecord(0, Guid.NewGuid(), 0, DateTime.UtcNow, 0)));
	}

	[Test]
	public void there_is_no_record_at_pos_zero()
	{
		var res = _chunk.TryReadAt(0, couldBeScavenged: true);
		Assert.IsFalse(res.Success);
	}

	[Test]
	public async Task there_is_no_first_record()
	{
		var res = await _chunk.TryReadFirst(CancellationToken.None);
		Assert.IsFalse(res.Success);
	}

	[Test]
	public void there_is_no_closest_forward_record_to_pos_zero()
	{
		var res = _chunk.TryReadClosestForward(0);
		Assert.IsFalse(res.Success);
	}

	[Test]
	public void there_is_no_closest_backward_record_from_end()
	{
		var res = _chunk.TryReadClosestForward(0);
		Assert.IsFalse(res.Success);
	}

	[Test]
	public async Task there_is_no_last_record()
	{
		var res = await _chunk.TryReadLast(CancellationToken.None);
		Assert.IsFalse(res.Success);
	}
}
