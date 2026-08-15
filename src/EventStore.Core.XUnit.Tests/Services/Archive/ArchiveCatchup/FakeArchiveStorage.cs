using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive.Naming;
using EventStore.Core.Services.Archive.Storage;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Plugins.Transforms;

namespace EventStore.Core.XUnit.Tests.Services.Archive.ArchiveCatchup;

internal class FakeArchiveStorage : IArchiveStorageWriter, IArchiveStorageReader, IArchiveStorageFactory
{
	private readonly int _chunkSize;
	private readonly string[] _chunks;
	private readonly long _checkpoint;
	private readonly CustomNamingStrategy _customNamingStrategy = new();

	public int NumListings => Interlocked.CompareExchange(ref _listings, 0, 0);
	private int _listings;

	private readonly Action<string> _onGetChunk;
	private readonly Action _onGetCheckpoint;
	private readonly Func<string, Stream> _getChunk;

	public string[] ChunkGets
	{
		get
		{
			lock (_chunkGets)
			{
				return _chunkGets.Order().ToArray();
			}
		}
	}

	private readonly List<string> _chunkGets;


	public FakeArchiveStorage(
		int chunkSize,
		string[] chunks,
		long checkpoint,
		Action<string> onGetChunk,
		Action onGetCheckpoint = null,
		Func<string, Stream> getChunk = null)
	{
		_chunkSize = chunkSize;
		_chunks = chunks;
		_checkpoint = checkpoint;
		_onGetChunk = onGetChunk;
		_onGetCheckpoint = onGetCheckpoint;
		_getChunk = getChunk;
		_chunkGets = new();
	}

	public IArchiveChunkNamer ChunkNamer { get; } = new ArchiveChunkNamer(new CustomNamingStrategy());

	public IArchiveStorageReader CreateReader() => this;
	public IArchiveStorageWriter CreateWriter() => this;

	public ValueTask<bool> StoreChunk(string chunkPath, string destinationFile, CancellationToken ct) => throw new NotImplementedException();

	public ValueTask<bool> SetCheckpoint(long checkpoint, CancellationToken ct) => throw new NotImplementedException();

	public ValueTask<Stream> GetChunk(string chunkFile, long start, long end, CancellationToken ct) =>
		throw new NotImplementedException();

	public ValueTask<long> GetCheckpoint(CancellationToken ct)
	{
		_onGetCheckpoint?.Invoke();
		return ValueTask.FromResult(_checkpoint);
	}

	public ValueTask<long> GetChunkLength(string chunkFile, CancellationToken ct)
	{
		return ValueTask.FromResult((long)TFChunk.GetAlignedSize(ChunkHeader.Size + ChunkFooter.Size));
	}

	private ChunkHeader CreateChunkHeader(int chunkStartNumber, int chunkEndNumber)
	{
		return new ChunkHeader(
			version: (int)TFChunk.ChunkVersions.Transformed,
			minCompatibleVersion: (int)TFChunk.ChunkVersions.Transformed,
			chunkSize: _chunkSize,
			chunkStartNumber,
			chunkEndNumber,
			isScavenged: false,
			chunkId: Guid.NewGuid(),
			transformType: TransformType.Identity);
	}

	public ValueTask<Stream> GetChunk(string chunkFile, CancellationToken ct)
	{
		lock (_chunkGets)
		{
			_chunkGets.Add(chunkFile);
		}

		_onGetChunk?.Invoke(chunkFile);
		if (_getChunk is not null)
		{
			return ValueTask.FromResult(_getChunk(chunkFile));
		}

		return ValueTask.FromResult(CreateChunk(chunkFile));
	}

	public Stream CreateChunk(string chunkFile)
	{
		return new MemoryStream(CreateChunkBytes(chunkFile));
	}

	public byte[] CreateChunkBytes(string chunkFile)
	{
		var chunk = new byte[TFChunk.GetAlignedSize(ChunkHeader.Size + ChunkFooter.Size)];
		var chunkStartNumber = _customNamingStrategy.GetIndexFor(chunkFile);
		var chunkEndNumber = _customNamingStrategy.GetVersionFor(chunkFile);
		var header = CreateChunkHeader(chunkStartNumber, chunkEndNumber);
		header.Format(chunk.AsSpan()[..ChunkHeader.Size]);

		var footerOffset = chunk.Length - ChunkFooter.Size;
		new ChunkFooter(true, physicalDataSize: 0, logicalDataSize: 0, mapSize: 0)
			.Format(chunk.AsSpan(footerOffset, ChunkFooter.Size));
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
		hash.AppendData(chunk.AsSpan(0, chunk.Length - ChunkFooter.ChecksumSize));
		new ChunkFooter(true, physicalDataSize: 0, logicalDataSize: 0, mapSize: 0, hash)
			.Format(chunk.AsSpan(footerOffset, ChunkFooter.Size));
		return chunk;
	}

	public IAsyncEnumerable<string> ListChunks(CancellationToken ct)
	{
		Interlocked.Increment(ref _listings);
		return _chunks.ToAsyncEnumerable();
	}
}
