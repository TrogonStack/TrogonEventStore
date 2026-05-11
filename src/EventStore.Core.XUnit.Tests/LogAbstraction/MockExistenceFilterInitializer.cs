using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.LogAbstraction;

namespace EventStore.Core.XUnit.Tests.LogAbstraction;

public class MockExistenceFilterInitializer(params string[] names) : INameExistenceFilterInitializer
{
	public ValueTask Initialize(INameExistenceFilter filter, long truncateToPosition, CancellationToken token)
	{
		int checkpoint = 0;
		foreach (var name in names)
		{
			filter.Add(name);
			filter.CurrentCheckpoint = checkpoint++;
		}

		return ValueTask.CompletedTask;
	}
}
