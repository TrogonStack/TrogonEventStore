using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Index;
using EventStore.Core.TransactionLog.Scavenging;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class AdHocIndexScavengerInterceptor(
	IIndexScavenger wrapped,
	Func<Func<IndexEntry, CancellationToken, ValueTask<bool>>, Func<IndexEntry, CancellationToken, ValueTask<bool>>> f)
	: IIndexScavenger
{
	public ValueTask ScavengeIndex(
		long scavengePoint,
		Func<IndexEntry, CancellationToken, ValueTask<bool>> shouldKeep,
		IIndexScavengerLog log,
		CancellationToken cancellationToken)
	{

		return wrapped.ScavengeIndex(scavengePoint, f(shouldKeep), log, cancellationToken);
	}
}
