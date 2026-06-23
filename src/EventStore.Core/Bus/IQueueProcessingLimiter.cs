using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Messaging;

namespace EventStore.Core.Bus;

public interface IQueueProcessingLimiter
{
	bool ShouldLimit(Message message);

	ValueTask<IDisposable> Acquire(CancellationToken token);
}
