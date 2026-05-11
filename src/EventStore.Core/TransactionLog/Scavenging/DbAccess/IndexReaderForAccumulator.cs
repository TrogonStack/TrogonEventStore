using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Storage.ReaderIndex;

namespace EventStore.Core.TransactionLog.Scavenging;

public class IndexReaderForAccumulator<TStreamId>(IReadIndex<TStreamId> readIndex)
	: IIndexReaderForAccumulator<TStreamId>
{
	// reads a stream forward but only returns event info not the full event.
	public ValueTask<IndexReadEventInfoResult> ReadEventInfoForward(
		StreamHandle<TStreamId> handle,
		long fromEventNumber,
		int maxCount,
		ScavengePoint scavengePoint,
		CancellationToken token)
	{

		switch (handle.Kind)
		{
			case StreamHandle.Kind.Hash:
				// uses the index only
				return readIndex.ReadEventInfoForward_NoCollisions(
					handle.StreamHash,
					fromEventNumber,
					maxCount,
					scavengePoint.Position,
					token);
			case StreamHandle.Kind.Id:
				// uses log to check for hash collisions
				return readIndex.ReadEventInfoForward_KnownCollisions(
					handle.StreamId,
					fromEventNumber,
					maxCount,
					scavengePoint.Position,
					token);
			default:
				return ValueTask.FromException<IndexReadEventInfoResult>(
					new ArgumentOutOfRangeException(nameof(handle), handle, null));
		}
	}

	// reads a stream backward but only returns event info not the full event.
	public ValueTask<IndexReadEventInfoResult> ReadEventInfoBackward(
		TStreamId streamId,
		StreamHandle<TStreamId> handle,
		long fromEventNumber,
		int maxCount,
		ScavengePoint scavengePoint,
		CancellationToken token)
	{

		switch (handle.Kind)
		{
			case StreamHandle.Kind.Hash:
				// uses the index only
				return readIndex.ReadEventInfoBackward_NoCollisions(
					handle.StreamHash,
					_ => streamId,
					fromEventNumber,
					maxCount,
					scavengePoint.Position,
					token);
			case StreamHandle.Kind.Id:
				// uses log to check for hash collisions
				return readIndex.ReadEventInfoBackward_KnownCollisions(
					handle.StreamId,
					fromEventNumber,
					maxCount,
					scavengePoint.Position,
					token);
			default:
				return ValueTask.FromException<IndexReadEventInfoResult>(
					new ArgumentOutOfRangeException(nameof(handle), handle, null));
		}
	}
}
