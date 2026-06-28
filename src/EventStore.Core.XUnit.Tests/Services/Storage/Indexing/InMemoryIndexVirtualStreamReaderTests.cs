using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Storage.Indexing;
using EventStore.Core.Services.Storage.InMemory;
using EventStore.Core.TransactionLog.LogRecords;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class InMemoryIndexVirtualStreamReaderTests
{
	private const string IndexStream = "$index-stream";

	[Fact]
	public void buffer_constructor_rejects_missing_stream_id()
	{
		var exception = Assert.Throws<ArgumentNullException>(() => new InMemoryIndexEventBuffer(null!));

		Assert.Equal("streamId", exception.ParamName);
	}

	[Fact]
	public void reader_constructor_rejects_missing_buffer()
	{
		var exception = Assert.Throws<ArgumentNullException>(() => new InMemoryIndexVirtualStreamReader(null!));

		Assert.Equal("buffer", exception.ParamName);
	}

	[Fact]
	public void append_rejects_missing_record()
	{
		var buffer = new InMemoryIndexEventBuffer(new IndexStreamId(IndexStream));

		var exception = Assert.Throws<ArgumentNullException>(() => buffer.Append(null!, 1));

		Assert.Equal("record", exception.ParamName);
	}

	[Fact]
	public void append_rejects_mismatched_stream_id()
	{
		var buffer = new InMemoryIndexEventBuffer(new IndexStreamId(IndexStream));

		var exception = Assert.Throws<ArgumentException>(() =>
			buffer.Append(CreateEventRecord("other-stream", 0, 1), 1));

		Assert.Equal("record", exception.ParamName);
	}

	[Fact]
	public void append_rejects_non_sequential_event_number()
	{
		var buffer = new InMemoryIndexEventBuffer(new IndexStreamId(IndexStream));
		buffer.Append(CreateEventRecord(IndexStream, 0, 1), 1);

		var exception = Assert.Throws<ArgumentException>(() =>
			buffer.Append(CreateEventRecord(IndexStream, 2, 3), 3));

		Assert.Equal("record", exception.ParamName);
	}

	[Fact]
	public async Task read_forwards_empty_returns_no_stream()
	{
		var reader = CreateReader();

		var result = await reader.ReadForwards(GenReadForwards(Guid.NewGuid(), 0, 10), CancellationToken.None);

		Assert.Equal(ReadStreamResult.NoStream, result.Result);
		Assert.Equal(-1, result.NextEventNumber);
		Assert.Equal(ExpectedVersion.NoStream, result.LastEventNumber);
		Assert.Equal(-1, result.TfLastCommitPosition);
		Assert.True(result.IsEndOfStream);
		Assert.Empty(result.Events);
	}

	[Fact]
	public async Task read_backwards_empty_returns_no_stream()
	{
		var reader = CreateReader();

		var result = await reader.ReadBackwards(GenReadBackwards(Guid.NewGuid(), 0, 10), CancellationToken.None);

		Assert.Equal(ReadStreamResult.NoStream, result.Result);
		Assert.Equal(-1, result.NextEventNumber);
		Assert.Equal(ExpectedVersion.NoStream, result.LastEventNumber);
		Assert.Equal(-1, result.TfLastCommitPosition);
		Assert.True(result.IsEndOfStream);
		Assert.Empty(result.Events);
	}

	[Fact]
	public async Task read_forwards_pages_events()
	{
		var buffer = CreateBufferWithEvents(5);
		var reader = new InMemoryIndexVirtualStreamReader(buffer);
		var correlation = Guid.NewGuid();

		var firstPage = await reader.ReadForwards(GenReadForwards(correlation, 0, 2), CancellationToken.None);
		Assert.Equal(ReadStreamResult.Success, firstPage.Result);
		Assert.Equal(2, firstPage.Events.Count);
		Assert.Equal(0, firstPage.Events[0].Event.EventNumber);
		Assert.Equal(1, firstPage.Events[1].Event.EventNumber);
		Assert.Equal(2, firstPage.NextEventNumber);
		Assert.Equal(4, firstPage.LastEventNumber);
		Assert.False(firstPage.IsEndOfStream);
		Assert.Equal(5, firstPage.TfLastCommitPosition);

		var secondPage = await reader.ReadForwards(
			GenReadForwards(correlation, firstPage.NextEventNumber, 2),
			CancellationToken.None);
		Assert.Equal(2, secondPage.Events.Count);
		Assert.Equal(2, secondPage.Events[0].Event.EventNumber);
		Assert.Equal(3, secondPage.Events[1].Event.EventNumber);
		Assert.Equal(4, secondPage.NextEventNumber);
		Assert.False(secondPage.IsEndOfStream);

		var finalPage = await reader.ReadForwards(
			GenReadForwards(correlation, secondPage.NextEventNumber, 2),
			CancellationToken.None);
		Assert.Single(finalPage.Events);
		Assert.Equal(4, finalPage.Events[0].Event.EventNumber);
		Assert.Equal(5, finalPage.NextEventNumber);
		Assert.True(finalPage.IsEndOfStream);
	}

	[Fact]
	public async Task read_forwards_treats_negative_from_as_zero()
	{
		var buffer = CreateBufferWithEvents(1);
		var reader = new InMemoryIndexVirtualStreamReader(buffer);

		var result = await reader.ReadForwards(GenReadForwards(Guid.NewGuid(), -1, 10), CancellationToken.None);

		Assert.Equal(ReadStreamResult.Success, result.Result);
		Assert.Single(result.Events);
		Assert.Equal(0, result.Events[0].Event.EventNumber);
	}

	[Fact]
	public async Task read_forwards_beyond_latest_event_returns_empty_success()
	{
		var buffer = CreateBufferWithEvents(1);
		var reader = new InMemoryIndexVirtualStreamReader(buffer);

		var result = await reader.ReadForwards(GenReadForwards(Guid.NewGuid(), 100, 10), CancellationToken.None);

		Assert.Equal(ReadStreamResult.Success, result.Result);
		Assert.Empty(result.Events);
		Assert.Equal(1, result.NextEventNumber);
		Assert.Equal(0, result.LastEventNumber);
		Assert.True(result.IsEndOfStream);
	}

	[Fact]
	public async Task read_backwards_pages_events_from_latest()
	{
		var buffer = CreateBufferWithEvents(5);
		var reader = new InMemoryIndexVirtualStreamReader(buffer);
		var correlation = Guid.NewGuid();

		var firstPage = await reader.ReadBackwards(GenReadBackwards(correlation, -1, 2), CancellationToken.None);
		Assert.Equal(ReadStreamResult.Success, firstPage.Result);
		Assert.Equal(2, firstPage.Events.Count);
		Assert.Equal(4, firstPage.Events[0].Event.EventNumber);
		Assert.Equal(3, firstPage.Events[1].Event.EventNumber);
		Assert.Equal(2, firstPage.NextEventNumber);
		Assert.Equal(4, firstPage.LastEventNumber);
		Assert.False(firstPage.IsEndOfStream);
		Assert.Equal(4, firstPage.FromEventNumber);

		var secondPage = await reader.ReadBackwards(
			GenReadBackwards(correlation, firstPage.NextEventNumber, 2),
			CancellationToken.None);
		Assert.Equal(2, secondPage.Events.Count);
		Assert.Equal(2, secondPage.Events[0].Event.EventNumber);
		Assert.Equal(1, secondPage.Events[1].Event.EventNumber);
		Assert.Equal(0, secondPage.NextEventNumber);
		Assert.False(secondPage.IsEndOfStream);

		var finalPage = await reader.ReadBackwards(
			GenReadBackwards(correlation, secondPage.NextEventNumber, 2),
			CancellationToken.None);
		Assert.Single(finalPage.Events);
		Assert.Equal(0, finalPage.Events[0].Event.EventNumber);
		Assert.Equal(-1, finalPage.NextEventNumber);
		Assert.True(finalPage.IsEndOfStream);
	}

	[Fact]
	public async Task read_backwards_from_explicit_position()
	{
		var buffer = CreateBufferWithEvents(2);
		var reader = new InMemoryIndexVirtualStreamReader(buffer);

		var result = await reader.ReadBackwards(GenReadBackwards(Guid.NewGuid(), 0, 10), CancellationToken.None);

		Assert.Equal(ReadStreamResult.Success, result.Result);
		Assert.Single(result.Events);
		Assert.Equal(0, result.Events[0].Event.EventNumber);
		Assert.Equal(-1, result.NextEventNumber);
		Assert.Equal(1, result.LastEventNumber);
		Assert.True(result.IsEndOfStream);
	}

	[Fact]
	public async Task read_backwards_beyond_latest_event_reads_from_latest()
	{
		var buffer = CreateBufferWithEvents(2);
		var reader = new InMemoryIndexVirtualStreamReader(buffer);

		var result = await reader.ReadBackwards(GenReadBackwards(Guid.NewGuid(), 100, 10), CancellationToken.None);

		Assert.Equal(ReadStreamResult.Success, result.Result);
		Assert.Equal(2, result.Events.Count);
		Assert.Equal(1, result.Events[0].Event.EventNumber);
		Assert.Equal(0, result.Events[1].Event.EventNumber);
		Assert.Equal(-1, result.NextEventNumber);
		Assert.Equal(1, result.LastEventNumber);
		Assert.True(result.IsEndOfStream);
		Assert.Equal(100, result.FromEventNumber);
	}

	[Fact]
	public void metadata_reflects_buffer_state()
	{
		var buffer = CreateBufferWithEvents(3);
		var reader = new InMemoryIndexVirtualStreamReader(buffer);

		Assert.Equal(2, reader.GetLastEventNumber(IndexStream));
		Assert.Equal(3, reader.GetLastIndexedPosition(IndexStream));
		Assert.Equal(ExpectedVersion.NoStream, reader.GetLastEventNumber("other-stream"));
		Assert.Equal(-1, reader.GetLastIndexedPosition("other-stream"));
	}

	[Fact]
	public void can_read_stream_routes_through_virtual_stream_reader()
	{
		var buffer = CreateBufferWithEvents(1);
		var reader = new InMemoryIndexVirtualStreamReader(buffer);
		var composite = new VirtualStreamReader([reader]);

		Assert.True(composite.CanReadStream(IndexStream));
		Assert.False(composite.CanReadStream("other-stream"));
		Assert.Equal(0, composite.GetLastEventNumber(IndexStream));
		Assert.Equal(1, composite.GetLastIndexedPosition(IndexStream));
	}

	private static InMemoryIndexVirtualStreamReader CreateReader() =>
		new(new InMemoryIndexEventBuffer(new IndexStreamId(IndexStream)));

	private static InMemoryIndexEventBuffer CreateBufferWithEvents(int count)
	{
		var buffer = new InMemoryIndexEventBuffer(new IndexStreamId(IndexStream));
		for (var i = 0; i < count; i++)
		{
			buffer.Append(CreateEventRecord(IndexStream, i, i + 1), i + 1);
		}

		return buffer;
	}

	private static EventRecord CreateEventRecord(string streamId, long number, long commitPosition) =>
		new(
			number,
			commitPosition,
			Guid.NewGuid(),
			Guid.NewGuid(),
			commitPosition,
			0,
			streamId,
			number - 1,
			DateTime.UtcNow,
			PrepareFlags.SingleWrite | PrepareFlags.IsCommitted | PrepareFlags.Data,
			$"event-{number}",
			Array.Empty<byte>(),
			Array.Empty<byte>());

	private static ClientMessage.ReadStreamEventsForward GenReadForwards(
		Guid correlation,
		long fromEventNumber,
		int maxCount,
		string eventStreamId = IndexStream) =>
		new(
			internalCorrId: correlation,
			correlationId: correlation,
			envelope: new NoopEnvelope(),
			eventStreamId: eventStreamId,
			fromEventNumber: fromEventNumber,
			maxCount: maxCount,
			resolveLinkTos: false,
			requireLeader: false,
			validationStreamVersion: null,
			user: ClaimsPrincipal.Current,
			replyOnExpired: true);

	private static ClientMessage.ReadStreamEventsBackward GenReadBackwards(
		Guid correlation,
		long fromEventNumber,
		int maxCount,
		string eventStreamId = IndexStream) =>
		new(
			internalCorrId: correlation,
			correlationId: correlation,
			envelope: new NoopEnvelope(),
			eventStreamId: eventStreamId,
			fromEventNumber: fromEventNumber,
			maxCount: maxCount,
			resolveLinkTos: false,
			requireLeader: false,
			validationStreamVersion: null,
			user: ClaimsPrincipal.Current);
}
