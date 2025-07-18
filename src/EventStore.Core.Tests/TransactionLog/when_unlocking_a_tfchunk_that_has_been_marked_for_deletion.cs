using System.IO;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class when_unlocking_a_tfchunk_that_has_been_marked_for_deletion : SpecificationWithFile
{
	private TFChunk _chunk;

	[SetUp]
	public override async Task SetUp()
	{
		await base.SetUp();
		_chunk = await TFChunkHelper.CreateNewChunk(Filename, 1000);
		var reader = _chunk.AcquireRawReader();
		_chunk.MarkForDeletion();
		reader.Release();
	}

	[Test]
	public void the_file_is_deleted()
	{
		Assert.IsFalse(File.Exists(Filename));
	}
}
