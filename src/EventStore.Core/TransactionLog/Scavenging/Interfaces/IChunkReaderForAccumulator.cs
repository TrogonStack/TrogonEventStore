using System.Collections.Generic;
using System.Threading;

namespace EventStore.Core.TransactionLog.Scavenging;

public interface IChunkReaderForAccumulator<TStreamId>
{
	// Each element in the enumerable indicates which of the three records has been populated for that iteration.
	IAsyncEnumerable<AccumulatorRecordType> ReadChunkInto(
		int logicalChunkNumber,
		RecordForAccumulator<TStreamId>.OriginalStreamRecord originalStreamRecord,
		RecordForAccumulator<TStreamId>.MetadataStreamRecord metadataStreamRecord,
		RecordForAccumulator<TStreamId>.TombStoneRecord tombStoneRecord,
		CancellationToken cancellationToken);
}
