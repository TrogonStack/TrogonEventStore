using System;
using System.Collections.Generic;
using System.Threading;
using EventStore.Core.TransactionLog.Scavenging;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class TracingChunkReaderForAccumulator<TStreamId>(
	IChunkReaderForAccumulator<TStreamId> wrapped,
	Action<string> trace)
	: IChunkReaderForAccumulator<TStreamId>
{
	public IAsyncEnumerable<AccumulatorRecordType> ReadChunkInto(
		int logicalChunkNumber,
		RecordForAccumulator<TStreamId>.OriginalStreamRecord originalStreamRecord,
		RecordForAccumulator<TStreamId>.MetadataStreamRecord metadataStreamRecord,
		RecordForAccumulator<TStreamId>.TombStoneRecord tombStoneRecord,
		CancellationToken cancellationToken)
	{

		var ret = wrapped.ReadChunkInto(
			logicalChunkNumber,
			originalStreamRecord,
			metadataStreamRecord,
			tombStoneRecord,
			cancellationToken);

		trace($"Reading Chunk {logicalChunkNumber}");
		return ret;
	}
}
