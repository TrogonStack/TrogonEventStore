using System.Threading.Tasks;
using EventStore.Core.Services.Monitoring.Stats;

namespace EventStore.Core.Bus;

public interface IQueuedHandler : IPublisher {
	string Name { get; }
	Task Start();
	Task Stop();

	void RequestStop();
	
	QueueStats GetStatistics();
}
