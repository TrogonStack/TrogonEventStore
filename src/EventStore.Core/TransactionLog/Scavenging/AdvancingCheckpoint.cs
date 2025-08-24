using System;
using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.TransactionLog.Scavenging;

public class AdvancingCheckpoint(Func<CancellationToken, ValueTask<long>> readCheckpoint)
{
	private long _cachedValue;

	public async ValueTask<bool> IsGreaterThanOrEqualTo(long position, CancellationToken ct)
	{
		if (_cachedValue >= position)
			return true;

		_cachedValue = await readCheckpoint(ct);

		return _cachedValue >= position;
	}

	public void Reset()
	{
		_cachedValue = 0;
	}
}
