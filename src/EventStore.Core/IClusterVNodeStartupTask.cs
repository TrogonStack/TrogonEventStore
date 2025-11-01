using System.Threading.Tasks;

namespace EventStore.Core;

public interface IClusterVNodeStartupTask
{
	Task Run();
}
