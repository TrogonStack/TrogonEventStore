using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class WhenReadingUncachedEmptyScavengedTfchunk : SpecificationWithFilePerTestFixture
{
	private TFChunk _chunk;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_chunk = await TFChunkHelper.CreateNewChunk(Filename, isScavenged: true);
		await _chunk.CompleteScavenge([], CancellationToken.None);
	}

	[OneTimeTearDown]
	public override void TestFixtureTearDown()
	{
		_chunk.Dispose();
		base.TestFixtureTearDown();
	}

	[Test]
	public void no_record_at_exact_position_can_be_read()
	{
		Assert.IsFalse(_chunk.TryReadAt(0, couldBeScavenged: true).Success);
	}

	[Test]
	public async Task no_record_can_be_read_as_first_record()
	{
		Assert.IsFalse((await _chunk.TryReadFirst(CancellationToken.None)).Success);
	}

	[Test]
	public void no_record_can_be_read_as_closest_forward_record()
	{
		Assert.IsFalse(_chunk.TryReadClosestForward(0).Success);
	}

	[Test]
	public async Task no_record_can_be_read_as_closest_backward_record() =>
		Assert.IsFalse((await _chunk.TryReadClosestBackward(0, CancellationToken.None)).Success);

	[Test]
	public async Task no_record_can_be_read_as_last_record() =>
		Assert.IsFalse((await _chunk.TryReadLast(CancellationToken.None)).Success);
}
