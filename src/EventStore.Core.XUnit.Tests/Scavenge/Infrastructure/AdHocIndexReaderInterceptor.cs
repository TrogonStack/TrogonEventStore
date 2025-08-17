using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.TransactionLog.Scavenging;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class AdHocIndexReaderInterceptor<TStreamId>(
	IIndexReaderForCalculator<TStreamId> wrapped,
	Func<
		Func<StreamHandle<TStreamId>, long, int, ScavengePoint, CancellationToken, ValueTask<IndexReadEventInfoResult>>,
		StreamHandle<TStreamId>, long, int, ScavengePoint, CancellationToken, ValueTask<IndexReadEventInfoResult>> f)
	: IIndexReaderForCalculator<TStreamId>
{
	public ValueTask<long> GetLastEventNumber(
		StreamHandle<TStreamId> streamHandle,
		ScavengePoint scavengePoint,
		CancellationToken token)
	{

		return wrapped.GetLastEventNumber(streamHandle, scavengePoint, token);
	}

	public ValueTask<IndexReadEventInfoResult> ReadEventInfoForward(
		StreamHandle<TStreamId> stream,
		long fromEventNumber,
		int maxCount,
		ScavengePoint scavengePoint,
		CancellationToken token)
	{

		return f(wrapped.ReadEventInfoForward, stream, fromEventNumber, maxCount, scavengePoint, token);
	}

	public ValueTask<bool> IsTombstone(long logPosition, CancellationToken token)
	{
		return wrapped.IsTombstone(logPosition, token);
	}
}
