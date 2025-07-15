using System.Threading;
using System.Threading.Tasks;
using DotNext;
using EventStore.Core.LogAbstraction;

namespace EventStore.Core.LogV3;

public class EventTypeLookupSystemTypesDecorator(INameLookup<uint> wrapped) : INameLookup<uint>
{
	public ValueTask<string> LookupName(uint eventTypeId, CancellationToken token)
	{
		return LogV3SystemEventTypes.TryGetVirtualEventType(eventTypeId, out var name)
			? new ValueTask<string>(name)
			: wrapped.LookupName(eventTypeId, token);
	}

	public ValueTask<Optional<uint>> TryGetLastValue(CancellationToken token)
		=> wrapped.TryGetLastValue(token);
}
