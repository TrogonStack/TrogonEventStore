using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Index;

namespace EventStore.Core.TransactionLog.Scavenging;

// the index executor performs the actual removal of the index entries
// for non-colliding streams this is index-only
public interface IIndexExecutor<TStreamId>
{
	ValueTask Execute(
		ScavengePoint scavengePoint,
		IScavengeStateForIndexExecutor<TStreamId> state,
		IIndexScavengerLog scavengerLogger,
		CancellationToken cancellationToken);

	ValueTask Execute(
		ScavengeCheckpoint.ExecutingIndex checkpoint,
		IScavengeStateForIndexExecutor<TStreamId> state,
		IIndexScavengerLog scavengerLogger,
		CancellationToken cancellationToken);
}
