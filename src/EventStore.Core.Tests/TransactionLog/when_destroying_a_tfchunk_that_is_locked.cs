using System.IO;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class when_destroying_a_tfchunk_that_is_locked : SpecificationWithFile
{
	private TFChunk _chunk;
	private TFChunkBulkReader _reader;

	[SetUp]
	public override async Task SetUp()
	{
		await base.SetUp();
		_chunk = await TFChunkHelper.CreateNewChunk(Filename, 1000);
		_chunk.Complete();
		_chunk.UnCacheFromMemory();
		_reader = _chunk.AcquireRawReader();
		_chunk.MarkForDeletion();
	}

	[TearDown]
	public override void TearDown()
	{
		_reader.Release();
		_chunk.MarkForDeletion();
		_chunk.WaitForDestroy(2000);
		base.TearDown();
	}

	[Test]
	public void the_file_is_not_deleted()
	{
		Assert.IsTrue(File.Exists(Filename));
	}
}
