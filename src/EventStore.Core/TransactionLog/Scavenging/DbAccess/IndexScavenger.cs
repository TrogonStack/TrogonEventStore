using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Index;

namespace EventStore.Core.TransactionLog.Scavenging;

public class IndexScavenger(ITableIndex tableIndex) : IIndexScavenger
{
	public ValueTask ScavengeIndex(
		long scavengePoint,
		Func<IndexEntry, CancellationToken, ValueTask<bool>> shouldKeep,
		IIndexScavengerLog log,
		CancellationToken cancellationToken) =>
		tableIndex.Scavenge(shouldKeep, log, cancellationToken);
}
