using System.Threading;
using System.Threading.Tasks;
using DotNext;

namespace EventStore.Core.TransactionLog.Scavenging;

public interface IChunkReaderForIndexExecutor<TStreamId>
{
	ValueTask<Optional<TStreamId>> TryGetStreamId(long position, CancellationToken token);
}
