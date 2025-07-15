using EventStore.Core.Exceptions;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.Transforms.Identity;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class WhenOpeningTfchunkFromNonExistingFile : SpecificationWithFile
{
	[Test]
	public void it_should_throw_a_file_not_found_exception()
	{
		Assert.ThrowsAsync<CorruptDatabaseException>(async () => await TFChunk.FromCompletedFile(Filename, verifyHash: true,
			unbufferedRead: false, reduceFileCachePressure: false, tracker: new TFChunkTracker.NoOp(),
			getTransformFactory: _ => new IdentityChunkTransformFactory()));
	}
}
