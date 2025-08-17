using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.TransactionLog.LogRecords;

namespace EventStore.Core.TransactionLog.Scavenging;

public class IndexReaderForCalculator<TStreamId>(
	IReadIndex<TStreamId> readIndex,
	Func<TFReaderLease> tfReaderFactory,
	Func<ulong, TStreamId> lookupUniqueHashUser)
	: IIndexReaderForCalculator<TStreamId>
{
	public ValueTask<long> GetLastEventNumber(
		StreamHandle<TStreamId> handle,
		ScavengePoint scavengePoint,
		CancellationToken token)
	{

		return handle.Kind switch
		{
			StreamHandle.Kind.Hash =>
				// tries as far as possible to use the index without consulting the log to fetch the last event number
				readIndex.GetStreamLastEventNumber_NoCollisions(handle.StreamHash, lookupUniqueHashUser,
					scavengePoint.Position, token),
			StreamHandle.Kind.Id =>
				// uses the index and the log to fetch the last event number
				readIndex.GetStreamLastEventNumber_KnownCollisions(handle.StreamId, scavengePoint.Position, token),
			_ => ValueTask.FromException<long>(new ArgumentOutOfRangeException(nameof(handle), handle, null))
		};
	}

	public ValueTask<IndexReadEventInfoResult> ReadEventInfoForward(
		StreamHandle<TStreamId> handle,
		long fromEventNumber,
		int maxCount,
		ScavengePoint scavengePoint,
		CancellationToken token)
	{

		return handle.Kind switch
		{
			StreamHandle.Kind.Hash =>
				// uses the index only
				readIndex.ReadEventInfoForward_NoCollisions(handle.StreamHash, fromEventNumber, maxCount,
					scavengePoint.Position, token),
			StreamHandle.Kind.Id =>
				// uses log to check for hash collisions
				readIndex.ReadEventInfoForward_KnownCollisions(handle.StreamId, fromEventNumber, maxCount,
					scavengePoint.Position, token),
			_ => ValueTask.FromException<IndexReadEventInfoResult>(
				new ArgumentOutOfRangeException(nameof(handle), handle, null))
		};
	}

	public async ValueTask<bool> IsTombstone(long logPosition, CancellationToken token)
	{
		using var reader = tfReaderFactory();
		var result = await reader.TryReadAt(logPosition, couldBeScavenged: true, token);

		if (!result.Success)
			return false;

		if (result.LogRecord is not IPrepareLogRecord prepare)
			throw new Exception(
				$"Incorrect type of log record {result.LogRecord.RecordType}, " +
				$"expected Prepare record.");

		return prepare.Flags.HasAnyOf(PrepareFlags.StreamDelete);
	}
}
