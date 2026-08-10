using System.Threading.Tasks;
using EventStore.Core.Index;
using NUnit.Framework;

namespace EventStore.Core.Tests.Index.IndexV1;

[TestFixture(PTableVersions.IndexV2)]
[TestFixture(PTableVersions.IndexV3)]
[TestFixture(PTableVersions.IndexV4)]
public class when_opening_a_ptable_with_one_reader : SpecificationWithFile
{
	private readonly byte _version;
	private PTable _ptable;

	public when_opening_a_ptable_with_one_reader(byte version)
	{
		_version = version;
	}

	[SetUp]
	public override async Task SetUp()
	{
		await base.SetUp();

		var memTable = new HashListMemTable(_version, maxSize: 1);
		memTable.Add(0x010100000000, 1, 42);
		_ptable = PTable.FromMemtable(
			memTable,
			Filename,
			initialReaders: 1,
			maxReaders: 1,
			cacheDepth: 16,
			skipIndexVerify: false,
			useBloomFilter: false,
			lruCacheSize: 0);
	}

	[TearDown]
	public override void TearDown()
	{
		_ptable.MarkForDestruction();
		_ptable.WaitForDisposal(1_000);
		base.TearDown();
	}

	[Test]
	public void reader_is_reusable_after_initialization()
	{
		Assert.That(_ptable.TryGetOneValue(0x010100000000, 1, out var position), Is.True);
		Assert.That(position, Is.EqualTo(42));
	}
}
