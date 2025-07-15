using System;
using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.LogAbstraction;

public interface INameExistenceFilter : IExistenceFilterReader<string>, IDisposable
{
	ValueTask Initialize(INameExistenceFilterInitializer source, long truncateToPosition, CancellationToken token);
	void TruncateTo(long checkpoint);
	void Verify(double corruptionThreshold);
	void Add(string name);
	void Add(ulong hash);
	long CurrentCheckpoint { get; set; }
}

public interface IExistenceFilterReader<T>
{
	bool MightContain(T item);
}
