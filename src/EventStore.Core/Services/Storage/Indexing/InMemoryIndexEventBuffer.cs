using System;
using System.Collections.Generic;
using EventStore.Core.Data;

namespace EventStore.Core.Services.Storage.Indexing;

public readonly record struct InMemoryIndexEventBufferSnapshot(
	ResolvedEvent[] Events,
	long LastEventNumber,
	long LastIndexedPosition);

public sealed class InMemoryIndexEventBuffer
{
	private readonly object _lock = new();
	private readonly List<ResolvedEvent> _events = [];
	private long _lastIndexedPosition = -1;

	public InMemoryIndexEventBuffer(IndexStreamId streamId)
	{
		ArgumentNullException.ThrowIfNull(streamId);
		StreamId = streamId;
	}

	public IndexStreamId StreamId { get; }

	public long LastEventNumber =>
		ReadLastEventNumber();

	public long LastIndexedPosition
	{
		get
		{
			lock (_lock)
			{
				return _lastIndexedPosition;
			}
		}
	}

	public void Append(EventRecord record, long? commitPosition)
	{
		ArgumentNullException.ThrowIfNull(record);

		if (record.EventStreamId != StreamId.Value)
		{
			throw new ArgumentException("Event stream id must match the index stream id.", nameof(record));
		}

		lock (_lock)
		{
			var expectedEventNumber = _events.Count;
			if (record.EventNumber != expectedEventNumber)
			{
				throw new ArgumentException(
					$"Event number must be appended sequentially starting at 0. Expected {expectedEventNumber}, got {record.EventNumber}.",
					nameof(record));
			}

			_events.Add(ResolvedEvent.ForUnresolvedEvent(record, commitPosition));

			if (commitPosition.HasValue)
			{
				_lastIndexedPosition = commitPosition.Value;
			}
		}
	}

	public InMemoryIndexEventBufferSnapshot CreateSnapshot()
	{
		lock (_lock)
		{
			return new InMemoryIndexEventBufferSnapshot(
				_events.ToArray(),
				ReadLastEventNumberCore(),
				_lastIndexedPosition);
		}
	}

	private long ReadLastEventNumber()
	{
		lock (_lock)
		{
			return ReadLastEventNumberCore();
		}
	}

	private long ReadLastEventNumberCore() =>
		_events.Count == 0 ? ExpectedVersion.NoStream : _events[^1].Event.EventNumber;
}
