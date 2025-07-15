using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Storage.ReaderIndex;

namespace EventStore.Core.TransactionLog.Scavenging;

public interface IIndexReaderForAccumulator<TStreamId>
{
	ValueTask<IndexReadEventInfoResult> ReadEventInfoForward(
		StreamHandle<TStreamId> handle,
		long fromEventNumber,
		int maxCount,
		ScavengePoint scavengePoint,
		CancellationToken token);

	ValueTask<IndexReadEventInfoResult> ReadEventInfoBackward(
		TStreamId streamId,
		StreamHandle<TStreamId> handle,
		long fromEventNumber,
		int maxCount,
		ScavengePoint scavengePoint,
		CancellationToken token);
}
