using EventStore.Core.Exceptions;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.Transforms.Identity;
using NUnit.Framework;
using System.IO;
using System.Threading;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class WhenOpeningTfchunkFromNonExistingFile : SpecificationWithFile
{
	[Test]
	public void it_should_throw_a_file_not_found_exception()
	{
		Assert.ThrowsAsync<CorruptDatabaseException>(async () => await TFChunk.FromCompletedFile(
			new ChunkLocalFileSystem(new VersionedPatternFileNamingStrategy(Path.GetDirectoryName(Filename), "chunk-")),
			Filename, verifyHash: true,
			unbufferedRead: false, reduceFileCachePressure: false, tracker: new TFChunkTracker.NoOp(),
			getTransformFactory: _ => new IdentityChunkTransformFactory()));
	}

	[Test]
	public void it_should_map_missing_parent_directory_to_chunk_not_found_when_opening_a_completed_chunk()
	{
		var missingDirectory = Path.Combine(Path.GetDirectoryName(Filename)!, "missing");
		var fileName = Path.Combine(missingDirectory, Path.GetFileName(Filename));

		var ex = Assert.ThrowsAsync<CorruptDatabaseException>(async () => await TFChunk.FromCompletedFile(
			new ChunkLocalFileSystem(new VersionedPatternFileNamingStrategy(missingDirectory, "chunk-")),
			fileName, verifyHash: true,
			unbufferedRead: false, reduceFileCachePressure: false, tracker: new TFChunkTracker.NoOp(),
			getTransformFactory: _ => new IdentityChunkTransformFactory()));

		Assert.That(ex?.InnerException, Is.TypeOf<ChunkNotFoundException>());
	}

	[Test]
	public void it_should_map_missing_parent_directory_to_chunk_not_found_when_reading_metadata()
	{
		var missingDirectory = Path.Combine(Path.GetDirectoryName(Filename)!, "missing");
		var fileName = Path.Combine(missingDirectory, Path.GetFileName(Filename));
		var fileSystem = new ChunkLocalFileSystem(new VersionedPatternFileNamingStrategy(missingDirectory, "chunk-"));

		var ex = Assert.ThrowsAsync<CorruptDatabaseException>(async () =>
			await fileSystem.ReadHeaderAsync(fileName, CancellationToken.None));

		Assert.That(ex?.InnerException, Is.TypeOf<ChunkNotFoundException>());
	}

	[Test]
	public void it_should_throw_when_metadata_file_is_too_small()
	{
		File.WriteAllBytes(Filename, new byte[ChunkHeader.Size]);
		var fileSystem = new ChunkLocalFileSystem(
			new VersionedPatternFileNamingStrategy(Path.GetDirectoryName(Filename)!, "chunk-"));

		var ex = Assert.ThrowsAsync<CorruptDatabaseException>(async () =>
			await fileSystem.ReadHeaderAsync(Filename, CancellationToken.None));

		Assert.That(ex?.InnerException, Is.TypeOf<BadChunkInDatabaseException>());
	}
}
