using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Messages;

namespace EventStore.Core.Services.Storage.InMemory;

public interface IVirtualStreamReader
{
	ValueTask<ClientMessage.ReadStreamEventsForwardCompleted> ReadForwards(
		ClientMessage.ReadStreamEventsForward msg,
		CancellationToken token);

	ValueTask<ClientMessage.ReadStreamEventsBackwardCompleted> ReadBackwards(
		ClientMessage.ReadStreamEventsBackward msg,
		CancellationToken token);

	long GetLastEventNumber(string streamId);

	long GetLastIndexedPosition(string streamId);

	bool CanReadStream(string streamId);
}
