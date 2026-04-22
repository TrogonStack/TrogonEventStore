using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Exceptions;
using EventStore.Core.TransactionLog.FileNamingStrategy;

namespace EventStore.Core.TransactionLog.Chunks;

public class TFChunkEnumerator : IChunkEnumerator
{
	private readonly IVersionedFileNamingStrategy _chunkFileNamingStrategy;
	private readonly Func<string, CancellationToken, ValueTask<ChunkHeader>> _readHeaderAsync;
	private string[] _allFiles = null;
	private readonly Dictionary<string, int> _nextChunkNumber = new();

	public TFChunkEnumerator(IVersionedFileNamingStrategy chunkFileNamingStrategy,
		Func<string, CancellationToken, ValueTask<ChunkHeader>> readHeaderAsync = null)
	{
		_chunkFileNamingStrategy = chunkFileNamingStrategy;
		_readHeaderAsync = readHeaderAsync;
	}

	public IAsyncEnumerable<TFChunkInfo> EnumerateChunks(int lastChunkNumber, CancellationToken token) =>
		EnumerateChunks(lastChunkNumber, getNextChunkNumber: null, token);

	public async IAsyncEnumerable<TFChunkInfo> EnumerateChunks(int lastChunkNumber,
		Func<string, int, int, CancellationToken, ValueTask<int>> getNextChunkNumber = null,
		[EnumeratorCancellation] CancellationToken token = default)
	{
		getNextChunkNumber ??= GetNextChunkNumber;

		if (_allFiles is null)
		{
			var allFiles = _chunkFileNamingStrategy.GetAllPresentFiles();
			Array.Sort(allFiles, StringComparer.CurrentCultureIgnoreCase);
			_allFiles = allFiles;
		}

		int expectedChunkNumber = 0;
		for (int i = 0; i < _allFiles.Length; i++)
		{
			var chunkFileName = _allFiles[i];
			var chunkNumber = _chunkFileNamingStrategy.GetIndexFor(Path.GetFileName(_allFiles[i]));
			var nextChunkNumber = -1;
			if (i + 1 < _allFiles.Length)
				nextChunkNumber = _chunkFileNamingStrategy.GetIndexFor(Path.GetFileName(_allFiles[i + 1]));

			if (chunkNumber < expectedChunkNumber)
			{
				// present in an earlier, merged, chunk
				yield return new OldVersion(chunkFileName, chunkNumber);
				continue;
			}

			if (chunkNumber > expectedChunkNumber)
			{
				// one or more chunks are missing
				for (int j = expectedChunkNumber; j < chunkNumber; j++)
				{
					yield return new MissingVersion(_chunkFileNamingStrategy.GetFilenameFor(j, 0), j);
				}

				// set the expected chunk number to prevent calling onFileMissing() again for the same chunk numbers
				expectedChunkNumber = chunkNumber;
			}

			if (chunkNumber == nextChunkNumber)
			{
				// there is a newer version of this chunk
				yield return new OldVersion(chunkFileName, chunkNumber);
			}
			else
			{
				// latest version of chunk with the expected chunk number
				expectedChunkNumber = await getNextChunkNumber(chunkFileName, chunkNumber,
					_chunkFileNamingStrategy.GetVersionFor(Path.GetFileName(chunkFileName)), token);
				yield return new LatestVersion(chunkFileName, chunkNumber, expectedChunkNumber - 1);
			}
		}

		for (int i = expectedChunkNumber; i <= lastChunkNumber; i++)
		{
			yield return new MissingVersion(_chunkFileNamingStrategy.GetFilenameFor(i, 0), i);
		}
	}

	private async ValueTask<int> GetNextChunkNumber(string chunkFileName, int chunkNumber, int chunkVersion,
		CancellationToken token)
	{
		if (chunkVersion is 0)
			return chunkNumber + 1;

		// we only cache next chunk numbers for chunks having a non-zero version
		if (_nextChunkNumber.TryGetValue(chunkFileName, out var nextChunkNumber))
			return nextChunkNumber;

		if (_readHeaderAsync is null)
			throw new InvalidOperationException("No chunk header reader was provided for versioned chunk enumeration.");

		var header = await _readHeaderAsync(chunkFileName, token);
		_nextChunkNumber[chunkFileName] = header.ChunkEndNumber + 1;
		return header.ChunkEndNumber + 1;
	}
}
