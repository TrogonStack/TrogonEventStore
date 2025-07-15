using System.Threading;
using System.Threading.Tasks;
using DotNext;
using EventStore.Core.LogAbstraction;
using EventStore.Core.Services;
using StreamId = System.UInt32;

namespace EventStore.Core.LogV3;

// Decorates a StreamNameLookup, intercepting Metastream (and VirtualStream) calls
public class StreamNameLookupMetastreamDecorator(
	INameLookup<StreamId> wrapped,
	IMetastreamLookup<StreamId> metastreams)
	: INameLookup<StreamId>
{
	public async ValueTask<string> LookupName(StreamId streamId, CancellationToken token)
	{
		if (metastreams.IsMetaStream(streamId))
		{
			streamId = metastreams.OriginalStreamOf(streamId);
			return await LookupName(streamId, token) is { } name
				? SystemStreams.MetastreamOf(name)
				: null;
		}
		else
		{
			return LogV3SystemStreams.TryGetVirtualStreamName(streamId, out var name)
				? name
				: await wrapped.LookupName(streamId, token);
		}
	}

	public ValueTask<Optional<StreamId>> TryGetLastValue(CancellationToken token)
	{
		return wrapped.TryGetLastValue(token);
	}
}
