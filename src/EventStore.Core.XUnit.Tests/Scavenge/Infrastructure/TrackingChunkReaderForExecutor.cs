using System.Collections.Generic;
using System.Threading;
using EventStore.Core.TransactionLog.Scavenging;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class TrackingChunkReaderForExecutor<TStreamId, TRecord>(
	IChunkReaderForExecutor<TStreamId, TRecord> wrapped,
	bool isRemote,
	Tracer tracer)
	:
		IChunkReaderForExecutor<TStreamId, TRecord>
{
	public string Name => wrapped.Name;

	public int FileSize => wrapped.FileSize;

	public int ChunkStartNumber => wrapped.ChunkStartNumber;

	public int ChunkEndNumber => wrapped.ChunkEndNumber;

	public bool IsReadOnly => wrapped.IsReadOnly;
	public bool IsRemote => isRemote;

	public long ChunkStartPosition => wrapped.ChunkStartPosition;

	public long ChunkEndPosition => wrapped.ChunkEndPosition;

	public IAsyncEnumerable<bool> ReadInto(
		RecordForExecutor<TStreamId, TRecord>.NonPrepare nonPrepare,
		RecordForExecutor<TStreamId, TRecord>.Prepare prepare,
		CancellationToken token)
	{

		tracer.Trace($"Opening Chunk {ChunkStartNumber}-{ChunkEndNumber}");
		return wrapped.ReadInto(nonPrepare, prepare, token);
	}
}
