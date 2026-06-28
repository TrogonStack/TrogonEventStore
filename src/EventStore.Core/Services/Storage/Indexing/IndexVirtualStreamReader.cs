using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Messages;
using EventStore.Core.Services.Storage.InMemory;

namespace EventStore.Core.Services.Storage.Indexing;

public abstract class IndexVirtualStreamReader : IVirtualStreamReader
{
	protected IndexVirtualStreamReader(IndexStreamId streamId)
	{
		ArgumentNullException.ThrowIfNull(streamId);
		StreamId = streamId;
	}

	public IndexStreamId StreamId { get; }

	public abstract ValueTask<ClientMessage.ReadStreamEventsForwardCompleted> ReadForwards(
		ClientMessage.ReadStreamEventsForward msg,
		CancellationToken token);

	public abstract ValueTask<ClientMessage.ReadStreamEventsBackwardCompleted> ReadBackwards(
		ClientMessage.ReadStreamEventsBackward msg,
		CancellationToken token);

	public abstract long GetLastEventNumber(string streamId);

	public abstract long GetLastIndexedPosition(string streamId);

	public bool CanReadStream(string streamId) => streamId == StreamId.Value;
}
