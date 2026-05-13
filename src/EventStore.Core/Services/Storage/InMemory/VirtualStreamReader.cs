using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Messages;

namespace EventStore.Core.Services.Storage.InMemory;

public class VirtualStreamReader : IVirtualStreamReader
{
	private readonly IVirtualStreamReader[] _readers;

	public VirtualStreamReader(IVirtualStreamReader[] readers)
	{
		_readers = readers;
	}

	public ValueTask<ClientMessage.ReadStreamEventsForwardCompleted> ReadForwards(
		ClientMessage.ReadStreamEventsForward msg,
		CancellationToken token)
	{
		if (TryGetReader(msg.EventStreamId, out var reader))
		{
			return reader.ReadForwards(msg, token);
		}

		return ValueTask.FromResult(new ClientMessage.ReadStreamEventsForwardCompleted(
			msg.CorrelationId,
			msg.EventStreamId,
			msg.FromEventNumber,
			msg.MaxCount,
			ReadStreamResult.NoStream,
			Array.Empty<ResolvedEvent>(),
			StreamMetadata.Empty,
			isCachePublic: false,
			error: string.Empty,
			nextEventNumber: -1,
			lastEventNumber: ExpectedVersion.NoStream,
			isEndOfStream: true,
			tfLastCommitPosition: -1));
	}

	public ValueTask<ClientMessage.ReadStreamEventsBackwardCompleted> ReadBackwards(
		ClientMessage.ReadStreamEventsBackward msg,
		CancellationToken token)
	{
		if (TryGetReader(msg.EventStreamId, out var reader))
		{
			return reader.ReadBackwards(msg, token);
		}

		return ValueTask.FromResult(new ClientMessage.ReadStreamEventsBackwardCompleted(
			msg.CorrelationId,
			msg.EventStreamId,
			msg.FromEventNumber,
			msg.MaxCount,
			ReadStreamResult.NoStream,
			Array.Empty<ResolvedEvent>(),
			streamMetadata: StreamMetadata.Empty,
			isCachePublic: false,
			error: string.Empty,
			nextEventNumber: -1,
			lastEventNumber: ExpectedVersion.NoStream,
			isEndOfStream: true,
			tfLastCommitPosition: -1));
	}

	public long GetLastEventNumber(string streamId) =>
		TryGetReader(streamId, out var reader) ? reader.GetLastEventNumber(streamId) : ExpectedVersion.NoStream;

	public long GetLastIndexedPosition(string streamId) =>
		TryGetReader(streamId, out var reader) ? reader.GetLastIndexedPosition(streamId) : -1;

	public bool CanReadStream(string streamId) => TryGetReader(streamId, out _);

	private bool TryGetReader(string streamId, out IVirtualStreamReader reader)
	{
		foreach (var candidate in _readers)
		{
			if (candidate.CanReadStream(streamId))
			{
				reader = candidate;
				return true;
			}
		}

		reader = null;
		return false;
	}
}
