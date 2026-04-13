using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class when_reading_cached_empty_scavenged_tfchunk : SpecificationWithFilePerTestFixture
{
	private TFChunk _chunk;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_chunk = await TFChunkHelper.CreateNewChunk(Filename, isScavenged: true);
		await _chunk.CompleteScavenge(new PosMap[0], CancellationToken.None);
		await _chunk.CacheInMemory(CancellationToken.None);
	}

	[OneTimeTearDown]
	public override void TestFixtureTearDown()
	{
		_chunk.Dispose();
		base.TestFixtureTearDown();
	}

	[Test]
	public async Task no_record_at_exact_position_can_be_read()
	{
		Assert.IsTrue(await _chunk.TryReadAt(0, couldBeScavenged: true, CancellationToken.None) is { Success: false });
	}

	[Test]
	public async Task empty_midpoint_cache_is_retained_while_chunk_data_is_cached()
	{
		await _chunk.TryReadAt(0, couldBeScavenged: true, CancellationToken.None);

		var readSide = typeof(TFChunk)
			.GetField("_readSide", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(_chunk);
		var midpoints = (Array)readSide!.GetType()
			.GetField("_midpoints", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(readSide)!;

		Assert.That(midpoints, Is.Empty);
	}

	[Test]
	public async Task no_record_can_be_read_as_first_record()
	{
		Assert.IsFalse((await _chunk.TryReadFirst(CancellationToken.None)).Success);
	}

	[Test]
	public async Task no_record_can_be_read_as_closest_forward_record()
	{
		Assert.IsTrue(await _chunk.TryReadClosestForward(0, CancellationToken.None) is { Success: false });
	}

	[Test]
	public async Task no_record_can_be_read_as_closest_backward_record() =>
		Assert.IsFalse((await _chunk.TryReadClosestBackward(0, CancellationToken.None)).Success);

	[Test]
	public async Task no_record_can_be_read_as_last_record() =>
		Assert.IsFalse((await _chunk.TryReadLast(CancellationToken.None)).Success);
}
