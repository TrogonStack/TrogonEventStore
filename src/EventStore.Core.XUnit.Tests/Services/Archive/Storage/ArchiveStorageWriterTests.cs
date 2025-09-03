using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive;
using EventStore.Core.Services.Archive.Storage.Exceptions;
using FluentStorage.Utils.Extensions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Storage;

[Collection("ArchiveStorageTests")]
public class ArchiveStorageWriterTests : ArchiveStorageTestsBase<ArchiveStorageWriterTests>
{
	[Theory]
	[InlineData(StorageType.FileSystem)]
	[InlineData(StorageType.S3, Skip = SkipS3)]
	public async Task can_store_a_chunk(StorageType storageType)
	{
		var sut = CreateWriterSut(storageType);
		var localChunk = CreateLocalChunk(0, 0);
		var destinationFile = Path.GetFileName(localChunk);
		Assert.True(await sut.StoreChunk(localChunk, destinationFile, CancellationToken.None));

		var localChunkContent = await File.ReadAllBytesAsync(localChunk);
		using var archivedChunkContent = await CreateReaderSut(storageType).GetChunk(destinationFile, CancellationToken.None);
		Assert.Equal(localChunkContent, archivedChunkContent.ToByteArray());
	}

	[Theory]
	[InlineData(StorageType.FileSystem)]
	[InlineData(StorageType.S3, Skip = SkipS3)]
	public async Task throws_chunk_deleted_exception_if_local_chunk_doesnt_exist(StorageType storageType)
	{
		var sut = CreateWriterSut(storageType);
		var localChunk = CreateLocalChunk(0, 0);
		var destinationFile = Path.GetFileName(localChunk);
		File.Delete(localChunk);
		await Assert.ThrowsAsync<ChunkDeletedException>(async () => await sut.StoreChunk(localChunk, destinationFile, CancellationToken.None));
	}
}
