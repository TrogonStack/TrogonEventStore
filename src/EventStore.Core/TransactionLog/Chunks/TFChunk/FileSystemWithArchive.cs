using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.TransactionLog.FileNamingStrategy;

namespace EventStore.Core.TransactionLog.Chunks.TFChunk;

public sealed class FileSystemWithArchive : IChunkFileSystem
{
	private readonly int _chunkSize;
	private readonly ILocatorCodec _locatorCodec;
	private readonly IChunkFileSystem _localFileSystem;
	private readonly IArchiveStorageReader _archive;

	public FileSystemWithArchive(
		int chunkSize,
		ILocatorCodec locatorCodec,
		IChunkFileSystem localFileSystem,
		IArchiveStorageReader archive)
	{
		_chunkSize = chunkSize;
		_locatorCodec = locatorCodec ?? throw new ArgumentNullException(nameof(locatorCodec));
		_localFileSystem = localFileSystem ?? throw new ArgumentNullException(nameof(localFileSystem));
		_archive = archive ?? throw new ArgumentNullException(nameof(archive));
	}

	public IVersionedFileNamingStrategy NamingStrategy => _localFileSystem.NamingStrategy;

	public ValueTask<IChunkHandle> OpenForReadAsync(string fileName, ReadOptimizationHint readOptimizationHint,
		bool asyncIO, CancellationToken token) =>
		_locatorCodec.Decode(fileName, out var logicalChunkNumber, out var localFileName)
			? ArchivedChunkHandle.OpenForReadAsync(_archive, logicalChunkNumber, token)
			: _localFileSystem.OpenForReadAsync(localFileName, readOptimizationHint, asyncIO, token);

	public async ValueTask<ChunkHeader> ReadHeaderAsync(string fileName, CancellationToken token)
	{
		if (!_locatorCodec.Decode(fileName, out var logicalChunkNumber, out var localFileName))
			return await _localFileSystem.ReadHeaderAsync(localFileName, token);

		using var handle = await ArchivedChunkHandle.OpenForReadAsync(_archive, logicalChunkNumber, token);
		return await ChunkFileReadHelper.ReadHeaderAsync(handle, fileName, token);
	}

	public async ValueTask<ChunkFooter> ReadFooterAsync(string fileName, CancellationToken token)
	{
		if (!_locatorCodec.Decode(fileName, out var logicalChunkNumber, out var localFileName))
			return await _localFileSystem.ReadFooterAsync(localFileName, token);

		using var handle = await ArchivedChunkHandle.OpenForReadAsync(_archive, logicalChunkNumber, token);
		return await ChunkFileReadHelper.ReadFooterAsync(handle, fileName, token);
	}

	public IChunkEnumerator CreateChunkEnumerator() =>
		new ChunkEnumeratorWithArchive(_chunkSize, _locatorCodec, _localFileSystem.CreateChunkEnumerator(), _archive);

	public void MoveFile(string sourceFileName, string destinationFileName) =>
		_localFileSystem.MoveFile(LocalMutationTarget(sourceFileName), LocalMutationTarget(destinationFileName));

	public void DeleteFile(string fileName) =>
		_localFileSystem.DeleteFile(LocalMutationTarget(fileName));

	public void SetAttributes(string fileName, FileAttributes fileAttributes) =>
		_localFileSystem.SetAttributes(LocalMutationTarget(fileName), fileAttributes);

	private string LocalMutationTarget(string fileName) =>
		_locatorCodec.Decode(fileName, out var logicalChunkNumber, out var localFileName)
			? throw new NotSupportedException($"Cannot mutate archived chunk {logicalChunkNumber}.")
			: localFileName;

	private sealed class ChunkEnumeratorWithArchive : IChunkEnumerator
	{
		private readonly int _chunkSize;
		private readonly ILocatorCodec _locatorCodec;
		private readonly IChunkEnumerator _localChunkEnumerator;
		private readonly IArchiveStorageReader _archive;

		public ChunkEnumeratorWithArchive(
			int chunkSize,
			ILocatorCodec locatorCodec,
			IChunkEnumerator localChunkEnumerator,
			IArchiveStorageReader archive)
		{
			_chunkSize = chunkSize;
			_locatorCodec = locatorCodec;
			_localChunkEnumerator = localChunkEnumerator;
			_archive = archive;
		}

		public async IAsyncEnumerable<TFChunkInfo> EnumerateChunks(int lastChunkNumber,
			[EnumeratorCancellation] CancellationToken token)
		{
			var firstChunkNotInArchive = (int)(await _archive.GetCheckpoint(token) / _chunkSize);

			await foreach (var chunkInfo in _localChunkEnumerator.EnumerateChunks(lastChunkNumber, token)
				               .WithCancellation(token))
			{
				if (chunkInfo is MissingVersion(_, var start) && start < firstChunkNotInArchive)
				{
					yield return new LatestVersion(_locatorCodec.EncodeRemote(start), start, start);
					continue;
				}

				yield return chunkInfo;
			}
		}
	}
}
