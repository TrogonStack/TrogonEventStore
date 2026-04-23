using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive.Storage.Exceptions;
using FluentStorage.Utils.Extensions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Storage;

[Collection("ArchiveStorageTests")]
public abstract class ArchiveStorageReaderTests<T> : ArchiveStorageTestsBase<T>
{
	[Fact]
	public async Task can_read_chunk_entirely()
	{
		var sut = CreateReaderSut(StorageType);

		// create a chunk and upload it
		var chunkPath = CreateLocalChunk(0, 0);
		var chunkFile = Path.GetFileName(chunkPath);
		await CreateWriterSut(StorageType).StoreChunk(chunkPath, chunkFile, CancellationToken.None);

		// read the local chunk
		var localContent = await File.ReadAllBytesAsync(chunkPath);

		// read the uploaded chunk
		using var chunkStream = await sut.GetChunk(chunkFile, CancellationToken.None);
		var chunkStreamContent = chunkStream.ToByteArray();

		// then
		Assert.Equal(localContent, chunkStreamContent);
	}

	[Fact]
	public async Task can_read_chunk_partially()
	{
		var sut = CreateReaderSut(StorageType);

		// create a chunk and upload it
		var chunkPath = CreateLocalChunk(0, 0);
		var chunkFile = Path.GetFileName(chunkPath);
		await CreateWriterSut(StorageType).StoreChunk(chunkPath, chunkFile, CancellationToken.None);

		// read the local chunk
		var localContent = await File.ReadAllBytesAsync(chunkPath);

		// read the uploaded chunk partially
		var start = localContent.Length / 2;
		var end = localContent.Length;
		using var chunkStream = await sut.GetChunk(chunkFile, start, end, CancellationToken.None);
		var chunkStreamContent = chunkStream.ToByteArray();

		// then
		Assert.Equal(localContent[start..end], chunkStreamContent);
	}

	[Fact]
	public async Task can_read_chunk_subrange_without_including_the_end_position()
	{
		var sut = CreateReaderSut(StorageType);

		var chunkPath = CreateLocalChunk(0, 0);
		var chunkFile = Path.GetFileName(chunkPath);
		await CreateWriterSut(StorageType).StoreChunk(chunkPath, chunkFile, CancellationToken.None);

		var localContent = await File.ReadAllBytesAsync(chunkPath);

		var start = localContent.Length / 4;
		var end = start + (localContent.Length / 4);
		using var chunkStream = await sut.GetChunk(chunkFile, start, end, CancellationToken.None);
		var chunkStreamContent = chunkStream.ToByteArray();

		Assert.Equal(localContent[start..end], chunkStreamContent);
	}

	[Fact]
	public async Task can_read_empty_range()
	{
		var sut = CreateReaderSut(StorageType);

		var chunkPath = CreateLocalChunk(0, 0);
		var chunkFile = Path.GetFileName(chunkPath);
		await CreateWriterSut(StorageType).StoreChunk(chunkPath, chunkFile, CancellationToken.None);

		using var chunkStream = await sut.GetChunk(chunkFile, 10, 10, CancellationToken.None);
		Assert.Empty(chunkStream.ToByteArray());
	}

	[Fact]
	public async Task reading_beyond_the_end_returns_an_empty_stream()
	{
		var sut = CreateReaderSut(StorageType);

		var chunkPath = CreateLocalChunk(0, 0);
		var chunkFile = Path.GetFileName(chunkPath);
		await CreateWriterSut(StorageType).StoreChunk(chunkPath, chunkFile, CancellationToken.None);

		var localContent = await File.ReadAllBytesAsync(chunkPath);
		var start = localContent.Length + 25;
		using var chunkStream = await sut.GetChunk(chunkFile, start, start + 50, CancellationToken.None);

		Assert.Empty(chunkStream.ToByteArray());
	}

	[Fact]
	public async Task reading_a_range_that_partially_overlaps_the_end_returns_available_bytes()
	{
		var sut = CreateReaderSut(StorageType);

		var chunkPath = CreateLocalChunk(0, 0);
		var chunkFile = Path.GetFileName(chunkPath);
		await CreateWriterSut(StorageType).StoreChunk(chunkPath, chunkFile, CancellationToken.None);

		var localContent = await File.ReadAllBytesAsync(chunkPath);
		var start = localContent.Length - 25;
		using var chunkStream = await sut.GetChunk(chunkFile, start, localContent.Length + 50, CancellationToken.None);

		Assert.Equal(localContent[start..], chunkStream.ToByteArray());
	}

	[Fact]
	public async Task reading_with_a_negative_start_throws_ArgumentOutOfRangeException()
	{
		var sut = CreateReaderSut(StorageType);

		var chunkPath = CreateLocalChunk(0, 0);
		var chunkFile = Path.GetFileName(chunkPath);
		await CreateWriterSut(StorageType).StoreChunk(chunkPath, chunkFile, CancellationToken.None);

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
		{
			using var _ = await sut.GetChunk(chunkFile, -1, 10, CancellationToken.None);
		});
	}

	[Fact]
	public async Task read_missing_chunk_throws_ChunkDeletedException()
	{
		var sut = CreateReaderSut(StorageType);

		await Assert.ThrowsAsync<ChunkDeletedException>(async () =>
		{
			using var _ = await sut.GetChunk("missing-chunk", CancellationToken.None);
		});
	}

	[Fact]
	public async Task partial_read_missing_chunk_throws_ChunkDeletedException()
	{
		var sut = CreateReaderSut(StorageType);

		await Assert.ThrowsAsync<ChunkDeletedException>(async () =>
		{
			using var _ = await sut.GetChunk("missing-chunk", 1, 2, CancellationToken.None);
		});
	}

	[Fact]
	public async Task can_list_chunks()
	{
		var sut = CreateReaderSut(StorageType);

		var chunk0 = CreateLocalChunk(0, 0);
		var chunk1 = CreateLocalChunk(1, 0);
		var chunk2 = CreateLocalChunk(2, 0);

		var writerSut = CreateWriterSut(StorageType);
		await writerSut.StoreChunk(chunk0, Path.GetFileName(chunk0), CancellationToken.None);
		await writerSut.StoreChunk(chunk1, Path.GetFileName(chunk1), CancellationToken.None);
		await writerSut.StoreChunk(chunk1, Path.GetFileName(chunk2), CancellationToken.None);

		var archivedChunks = sut.ListChunks(CancellationToken.None).ToEnumerable();
		Assert.Equal([
			Path.GetFileName(chunk0),
			Path.GetFileName(chunk1),
			Path.GetFileName(chunk2)
		], archivedChunks);
	}

	[Fact]
	public async Task listing_ignores_files_outside_the_chunk_prefix()
	{
		var sut = CreateReaderSut(StorageType);

		var chunk = CreateLocalChunk(0, 0);
		var writerSut = CreateWriterSut(StorageType);
		await writerSut.StoreChunk(chunk, Path.GetFileName(chunk), CancellationToken.None);
		await writerSut.StoreChunk(chunk, "other-000000.000000", CancellationToken.None);

		var archivedChunks = sut.ListChunks(CancellationToken.None).ToEnumerable();
		Assert.Equal([Path.GetFileName(chunk)], archivedChunks);
	}
}
