using System;
using System.Threading;
using System.Threading.Tasks;
using DotNext;
using EventStore.Core.TransactionLog.LogRecords;

namespace EventStore.Core.TransactionLog.Scavenging;

public class ChunkReaderForIndexExecutor<TStreamId>(Func<TFReaderLease> tfReaderFactory)
	: IChunkReaderForIndexExecutor<TStreamId>
{
	public async ValueTask<Optional<TStreamId>> TryGetStreamId(long position, CancellationToken token)
	{
		using var reader = tfReaderFactory();
		var result = await reader.TryReadAt(position, couldBeScavenged: true, token);
		if (!result.Success)
		{
			return Optional.None<TStreamId>();
		}

		if (result.LogRecord is not IPrepareLogRecord<TStreamId> prepare)
			throw new Exception($"Record in index at position {position} is not a prepare");

		return prepare.EventStreamId;
	}
}
