using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Messages;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed class InMemoryIndexVirtualStreamReader : IndexVirtualStreamReader
{
	private readonly InMemoryIndexEventBuffer _buffer;

	public InMemoryIndexVirtualStreamReader(InMemoryIndexEventBuffer buffer)
		: base(GetStreamId(buffer))
	{
		_buffer = buffer;
	}

	public override ValueTask<ClientMessage.ReadStreamEventsForwardCompleted> ReadForwards(
		ClientMessage.ReadStreamEventsForward msg,
		CancellationToken token)
	{
		var snapshot = _buffer.CreateSnapshot();
		var lastEventNumber = snapshot.LastEventNumber;
		var tfLastCommitPosition = snapshot.LastIndexedPosition;

		if (lastEventNumber == ExpectedVersion.NoStream)
		{
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
				tfLastCommitPosition: tfLastCommitPosition));
		}

		var fromEventNumber = msg.FromEventNumber < 0 ? 0 : msg.FromEventNumber;
		var maxCount = Math.Max(0, msg.MaxCount);

		if (fromEventNumber > lastEventNumber || maxCount == 0)
		{
			var emptyNextEventNumber = fromEventNumber > lastEventNumber ? lastEventNumber + 1 : fromEventNumber;
			var emptyIsEndOfStream = fromEventNumber > lastEventNumber;

			return ValueTask.FromResult(new ClientMessage.ReadStreamEventsForwardCompleted(
				msg.CorrelationId,
				msg.EventStreamId,
				msg.FromEventNumber,
				msg.MaxCount,
				ReadStreamResult.Success,
				Array.Empty<ResolvedEvent>(),
				StreamMetadata.Empty,
				isCachePublic: false,
				error: string.Empty,
				nextEventNumber: emptyNextEventNumber,
				lastEventNumber: lastEventNumber,
				isEndOfStream: emptyIsEndOfStream,
				tfLastCommitPosition: tfLastCommitPosition));
		}

		var eventCount = (int)Math.Min(maxCount, lastEventNumber - fromEventNumber + 1);
		var events = new ResolvedEvent[eventCount];
		for (var index = 0; index < events.Length; index++)
		{
			events[index] = snapshot.Events[(int)(fromEventNumber + index)];
		}

		var nextEventNumber = fromEventNumber + events.Length;
		var isEndOfStream = nextEventNumber > lastEventNumber;

		return ValueTask.FromResult(new ClientMessage.ReadStreamEventsForwardCompleted(
			msg.CorrelationId,
			msg.EventStreamId,
			msg.FromEventNumber,
			msg.MaxCount,
			ReadStreamResult.Success,
			events,
			StreamMetadata.Empty,
			isCachePublic: false,
			error: string.Empty,
			nextEventNumber: nextEventNumber,
			lastEventNumber: lastEventNumber,
			isEndOfStream: isEndOfStream,
			tfLastCommitPosition: tfLastCommitPosition));
	}

	public override ValueTask<ClientMessage.ReadStreamEventsBackwardCompleted> ReadBackwards(
		ClientMessage.ReadStreamEventsBackward msg,
		CancellationToken token)
	{
		var snapshot = _buffer.CreateSnapshot();
		var lastEventNumber = snapshot.LastEventNumber;
		var tfLastCommitPosition = snapshot.LastIndexedPosition;

		if (lastEventNumber == ExpectedVersion.NoStream)
		{
			return ValueTask.FromResult(new ClientMessage.ReadStreamEventsBackwardCompleted(
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
				tfLastCommitPosition: tfLastCommitPosition));
		}

		var requestedEventNumber = msg.FromEventNumber < 0 ? lastEventNumber : msg.FromEventNumber;
		var readFromEventNumber = Math.Min(requestedEventNumber, lastEventNumber);
		var maxCount = Math.Max(0, msg.MaxCount);

		var eventCount = (int)Math.Min(maxCount, readFromEventNumber + 1);
		var events = new ResolvedEvent[eventCount];
		for (var index = 0; index < events.Length; index++)
		{
			events[index] = snapshot.Events[(int)(readFromEventNumber - index)];
		}

		var nextEventNumber = readFromEventNumber - events.Length;
		var isEndOfStream = nextEventNumber < 0;
		if (isEndOfStream)
		{
			nextEventNumber = -1;
		}

		return ValueTask.FromResult(new ClientMessage.ReadStreamEventsBackwardCompleted(
			msg.CorrelationId,
			msg.EventStreamId,
			requestedEventNumber,
			msg.MaxCount,
			ReadStreamResult.Success,
			events,
			StreamMetadata.Empty,
			isCachePublic: false,
			error: string.Empty,
			nextEventNumber: nextEventNumber,
			lastEventNumber: lastEventNumber,
			isEndOfStream: isEndOfStream,
			tfLastCommitPosition: tfLastCommitPosition));
	}

	public override long GetLastEventNumber(string streamId) =>
		CanReadStream(streamId) ? _buffer.LastEventNumber : ExpectedVersion.NoStream;

	public override long GetLastIndexedPosition(string streamId) =>
		CanReadStream(streamId) ? _buffer.LastIndexedPosition : -1;

	private static IndexStreamId GetStreamId(InMemoryIndexEventBuffer buffer)
	{
		ArgumentNullException.ThrowIfNull(buffer);
		return buffer.StreamId;
	}
}
