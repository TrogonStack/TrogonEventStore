using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.LogAbstraction;

public interface INameExistenceFilterInitializer
{
	ValueTask Initialize(INameExistenceFilter filter, long truncateToPosition, CancellationToken token);
}
