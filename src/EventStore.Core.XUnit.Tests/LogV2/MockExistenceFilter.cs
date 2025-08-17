using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Index.Hashes;
using EventStore.Core.LogAbstraction;

namespace EventStore.Core.XUnit.Tests.LogV2;

public class MockExistenceFilter(ILongHasher<string> hasher, int addDelayMs = 0) : INameExistenceFilter
{
	public HashSet<ulong> Hashes { get; } = new();

	public long CurrentCheckpoint { get; set; } = -1;

	public void Add(string name)
	{
		if (addDelayMs > 0)
			Thread.Sleep(addDelayMs);
		Hashes.Add(hasher.Hash(name));
	}

	public void Add(ulong hash)
	{
		if (addDelayMs > 0)
			Thread.Sleep(addDelayMs);
		Hashes.Add(hash);
	}

	public void Dispose()
	{
	}

	public ValueTask Initialize(INameExistenceFilterInitializer source, long truncateToPosition,
		CancellationToken token)
		=> source.Initialize(this, truncateToPosition, token);

	public void TruncateTo(long checkpoint)
	{
		CurrentCheckpoint = checkpoint;
	}

	public void Verify(double corruptionThreshold) { }

	public bool MightContain(string name)
	{
		return Hashes.Contains(hasher.Hash(name));
	}
}
