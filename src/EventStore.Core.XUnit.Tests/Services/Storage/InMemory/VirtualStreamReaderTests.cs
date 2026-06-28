using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Core.Services.Storage.InMemory;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.InMemory;

public class VirtualStreamReaderTests
{
	private readonly VirtualStreamReader _sut;
	private readonly NodeStateListenerService _listener;

	public VirtualStreamReaderTests()
	{
		var channel = Channel.CreateUnbounded<Message>();
		_listener = new NodeStateListenerService(
			new EnvelopePublisher(new ChannelEnvelope(channel)),
			new InMemoryLog());
		_sut = new VirtualStreamReader([_listener.Stream]);
	}

	[Fact]
	public void constructor_rejects_missing_readers()
	{
		var exception = Assert.Throws<ArgumentNullException>(() => new VirtualStreamReader(null!));

		Assert.Equal("readers", exception.ParamName);
	}

	[Fact]
	public void constructor_rejects_missing_reader()
	{
		var exception = Assert.Throws<ArgumentException>(() => new VirtualStreamReader([null!]));

		Assert.Equal("readers", exception.ParamName);
		Assert.Equal("Virtual stream readers cannot contain null. (Parameter 'readers')", exception.Message);
	}

	private static ClientMessage.ReadStreamEventsBackward GenReadBackwards(
		Guid correlation,
		long fromEventNumber,
		int maxCount,
		string eventStreamId = SystemStreams.NodeStateStream)
	{
		return new ClientMessage.ReadStreamEventsBackward(
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

	public static ClientMessage.ReadStreamEventsForward GenReadForwards(
		Guid correlation,
		long fromEventNumber,
		int maxCount,
		string eventStreamId = SystemStreams.NodeStateStream)
	{
		return new ClientMessage.ReadStreamEventsForward(
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
	}

	public class ReadForwardEmptyTests : VirtualStreamReaderTests
	{
		[Fact]
		public async Task read_forwards_empty()
		{
			var correlation = Guid.NewGuid();

			var result = await _sut.ReadForwards(GenReadForwards(correlation, fromEventNumber: 0, maxCount: 10),
				CancellationToken.None);

			Assert.Equal(correlation, result.CorrelationId);
			Assert.Equal(ReadStreamResult.NoStream, result.Result);
			Assert.Equal(SystemStreams.NodeStateStream, result.EventStreamId);
			Assert.Equal(0, result.FromEventNumber);
			Assert.Equal(10, result.MaxCount);
			Assert.Equal(-1, result.NextEventNumber);
			Assert.Equal(-1, result.LastEventNumber);
			Assert.True(result.IsEndOfStream);
			Assert.Empty(result.Events);
		}
	}

	public class MissingReaderTests : VirtualStreamReaderTests
	{
		private const string UnknownStream = "$mem-unknown";

		[Fact]
		public async Task read_forwards_returns_no_stream_when_no_reader_claims_the_stream()
		{
			var correlation = Guid.NewGuid();

			var result = await _sut.ReadForwards(GenReadForwards(correlation, fromEventNumber: 0, maxCount: 10, UnknownStream),
				CancellationToken.None);

			Assert.Equal(correlation, result.CorrelationId);
			Assert.Equal(ReadStreamResult.NoStream, result.Result);
			Assert.Equal(UnknownStream, result.EventStreamId);
			Assert.Equal(ExpectedVersion.NoStream, result.LastEventNumber);
			Assert.True(result.IsEndOfStream);
			Assert.Empty(result.Events);
		}

		[Fact]
		public async Task read_backwards_returns_no_stream_when_no_reader_claims_the_stream()
		{
			var correlation = Guid.NewGuid();

			var result = await _sut.ReadBackwards(GenReadBackwards(correlation, fromEventNumber: 0, maxCount: 10, UnknownStream),
				CancellationToken.None);

			Assert.Equal(correlation, result.CorrelationId);
			Assert.Equal(ReadStreamResult.NoStream, result.Result);
			Assert.Equal(UnknownStream, result.EventStreamId);
			Assert.Equal(ExpectedVersion.NoStream, result.LastEventNumber);
			Assert.True(result.IsEndOfStream);
			Assert.Empty(result.Events);
		}
	}

	public class ReadForwardTests : VirtualStreamReaderTests
	{
		[Fact]
		public async Task read_forwards()
		{
			_listener.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
			var correlation = Guid.NewGuid();

			var result = await _sut.ReadForwards(GenReadForwards(correlation, fromEventNumber: 0, maxCount: 10),
				CancellationToken.None);

			Assert.Equal(correlation, result.CorrelationId);
			Assert.Equal(ReadStreamResult.Success, result.Result);
			Assert.Equal(SystemStreams.NodeStateStream, result.EventStreamId);
			Assert.Equal(0, result.FromEventNumber);
			Assert.Equal(10, result.MaxCount);
			Assert.Equal(1, result.NextEventNumber);
			Assert.Equal(0, result.LastEventNumber);
			Assert.True(result.IsEndOfStream);
			Assert.Single(result.Events);

			var @event = result.Events[0];
			Assert.Equal(0, @event.Event.EventNumber);
			Assert.Equal(SystemStreams.NodeStateStream, @event.Event.EventStreamId);
			Assert.Equal(NodeStateListenerService.EventType, @event.Event.EventType);
		}

		[Fact]
		public async Task read_forwards_beyond_latest_event()
		{
			_listener.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
			var correlation = Guid.NewGuid();

			var result = await _sut.ReadForwards(GenReadForwards(correlation, fromEventNumber: 1000, maxCount: 10),
				CancellationToken.None);

			Assert.Equal(correlation, result.CorrelationId);
			Assert.Equal(ReadStreamResult.Success, result.Result);
			Assert.Equal(SystemStreams.NodeStateStream, result.EventStreamId);
			Assert.Equal(1_000, result.FromEventNumber);
			Assert.Equal(10, result.MaxCount);
			Assert.Equal(1, result.NextEventNumber);
			Assert.Equal(0, result.LastEventNumber);
			Assert.True(result.IsEndOfStream);
			Assert.Empty(result.Events);
		}

		[Fact]
		public async Task read_forwards_below_latest_event()
		{
			_listener.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
			_listener.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
			var correlation = Guid.NewGuid();

			var result = await _sut.ReadForwards(GenReadForwards(correlation, fromEventNumber: 0, maxCount: 10),
				CancellationToken.None);

			Assert.Equal(correlation, result.CorrelationId);
			Assert.Equal(ReadStreamResult.Success, result.Result);
			Assert.Equal(SystemStreams.NodeStateStream, result.EventStreamId);
			Assert.Equal(0, result.FromEventNumber);
			Assert.Equal(10, result.MaxCount);
			Assert.Equal(2, result.NextEventNumber);
			Assert.Equal(1, result.LastEventNumber);
			Assert.True(result.IsEndOfStream);
			Assert.Single(result.Events);

			var @event = result.Events[0];
			Assert.Equal(1, @event.Event.EventNumber);
			Assert.Equal(SystemStreams.NodeStateStream, @event.Event.EventStreamId);
			Assert.Equal(NodeStateListenerService.EventType, @event.Event.EventType);
		}

		[Fact]
		public async Task read_forwards_far_below_latest_event()
		{
			// we specify maxCount, not an upper event number, so it is acceptable in this case to either
			// - find event 49 (like we do for regular stream forward maxAge reads if old events have been scavenged)
			//   and reach the end of the stream (nextEventNumber == 50)
			// - not find event 49 (like we do for regular maxCount reads, even if old events have been scavenged)
			//   and not reach the end of the stream (nextEventNumber <= 49 so that we can read it in subsequent pages)
			// current implementation finds the event.
			for (var i = 0; i < 50; i++)
			{
				_listener.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
			}

			var correlation = Guid.NewGuid();

			var result = await _sut.ReadForwards(GenReadForwards(correlation, fromEventNumber: 0, maxCount: 10),
				CancellationToken.None);

			Assert.Equal(correlation, result.CorrelationId);
			Assert.Equal(ReadStreamResult.Success, result.Result);
			Assert.Equal(SystemStreams.NodeStateStream, result.EventStreamId);
			Assert.Equal(0, result.FromEventNumber);
			Assert.Equal(10, result.MaxCount);
			Assert.Equal(50, result.NextEventNumber);
			Assert.Equal(49, result.LastEventNumber);
			Assert.True(result.IsEndOfStream);
			Assert.Single(result.Events);

			var @event = result.Events[0];
			Assert.Equal(49, @event.Event.EventNumber);
			Assert.Equal(SystemStreams.NodeStateStream, @event.Event.EventStreamId);
			Assert.Equal(NodeStateListenerService.EventType, @event.Event.EventType);
		}
	}

	public class ReadBackwardsEmptyTests : VirtualStreamReaderTests
	{
		[Fact]
		public async Task read_backwards_empty()
		{
			var correlation = Guid.NewGuid();

			var result = await _sut.ReadBackwards(GenReadBackwards(correlation, fromEventNumber: 0, maxCount: 10),
				CancellationToken.None);

			Assert.Equal(correlation, result.CorrelationId);
			Assert.Equal(ReadStreamResult.NoStream, result.Result);
			Assert.Equal(SystemStreams.NodeStateStream, result.EventStreamId);
			Assert.Equal(0, result.FromEventNumber);
			Assert.Equal(10, result.MaxCount);
			Assert.Equal(-1, result.NextEventNumber);
			Assert.Equal(-1, result.LastEventNumber);
			Assert.True(result.IsEndOfStream);
			Assert.Empty(result.Events);
		}
	}

	public class ReadBackwardsTests : VirtualStreamReaderTests
	{
		[Fact]
		public async Task read_backwards()
		{
			_listener.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
			var correlation = Guid.NewGuid();

			var result = await _sut.ReadBackwards(GenReadBackwards(correlation, fromEventNumber: 0, maxCount: 10),
				CancellationToken.None);

			Assert.Equal(correlation, result.CorrelationId);
			Assert.Equal(ReadStreamResult.Success, result.Result);
			Assert.Equal(SystemStreams.NodeStateStream, result.EventStreamId);
			Assert.Equal(0, result.FromEventNumber);
			Assert.Equal(10, result.MaxCount);
			Assert.Equal(-1, result.NextEventNumber);
			Assert.Equal(0, result.LastEventNumber);
			Assert.True(result.IsEndOfStream);
			Assert.Single(result.Events);

			var @event = result.Events[0];
			Assert.Equal(0, @event.Event.EventNumber);
			Assert.Equal(SystemStreams.NodeStateStream, @event.Event.EventStreamId);
			Assert.Equal(NodeStateListenerService.EventType, @event.Event.EventType);
		}

		[Fact]
		public async Task read_backwards_beyond_latest_event()
		{
			_listener.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
			var correlation = Guid.NewGuid();

			var result = await _sut.ReadBackwards(GenReadBackwards(correlation, fromEventNumber: 5, maxCount: 10),
				CancellationToken.None);

			Assert.Equal(correlation, result.CorrelationId);
			Assert.Equal(ReadStreamResult.Success, result.Result);
			Assert.Equal(SystemStreams.NodeStateStream, result.EventStreamId);
			Assert.Equal(5, result.FromEventNumber);
			Assert.Equal(10, result.MaxCount);
			Assert.Equal(-1, result.NextEventNumber);
			Assert.Equal(0, result.LastEventNumber);
			Assert.True(result.IsEndOfStream);
			Assert.Single(result.Events);

			var @event = result.Events[0];
			Assert.Equal(0, @event.Event.EventNumber);
			Assert.Equal(SystemStreams.NodeStateStream, @event.Event.EventStreamId);
			Assert.Equal(NodeStateListenerService.EventType, @event.Event.EventType);
		}

		[Fact]
		public async Task read_backwards_far_beyond_latest_event()
		{
			// we specify maxCount, not a lower event number, so it is acceptable in this case to either
			// - find event 0 (like we do for regular stream forward maxAge reads if old events have been scavenged)
			//   and reach the end of the stream (nextEventNumber == -1)
			// - not find event 0 (like we do for regular maxCount reads, even if old events have been scavenged)
			//   and not reach the end of the stream (nextEventNumber >= 0 so that we can read it in subsequent pages)
			// current implementation finds the event.
			_listener.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
			var correlation = Guid.NewGuid();

			var result = await _sut.ReadBackwards(GenReadBackwards(correlation, fromEventNumber: 1000, maxCount: 10),
				CancellationToken.None);

			Assert.Equal(correlation, result.CorrelationId);
			Assert.Equal(ReadStreamResult.Success, result.Result);
			Assert.Equal(SystemStreams.NodeStateStream, result.EventStreamId);
			Assert.Equal(1_000, result.FromEventNumber);
			Assert.Equal(10, result.MaxCount);
			Assert.Equal(-1, result.NextEventNumber);
			Assert.Equal(0, result.LastEventNumber);
			Assert.True(result.IsEndOfStream);
			Assert.Single(result.Events);

			var @event = result.Events[0];
			Assert.Equal(0, @event.Event.EventNumber);
			Assert.Equal(SystemStreams.NodeStateStream, @event.Event.EventStreamId);
			Assert.Equal(NodeStateListenerService.EventType, @event.Event.EventType);
		}

		[Fact]
		public async Task read_backwards_below_latest_event()
		{
			_listener.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
			_listener.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
			var correlation = Guid.NewGuid();

			var result = await _sut.ReadBackwards(GenReadBackwards(correlation, fromEventNumber: 0, maxCount: 10),
				CancellationToken.None);

			Assert.Equal(correlation, result.CorrelationId);
			Assert.Equal(ReadStreamResult.Success, result.Result);
			Assert.Equal(SystemStreams.NodeStateStream, result.EventStreamId);
			Assert.Equal(0, result.FromEventNumber);
			Assert.Equal(10, result.MaxCount);
			Assert.Equal(-1, result.NextEventNumber);
			Assert.Equal(1, result.LastEventNumber);
			Assert.True(result.IsEndOfStream);
			Assert.Empty(result.Events);
		}
	}

	public class MultipleReaderTests
	{
		private const string FirstStream = "$mem-first";
		private const string SecondStream = "$mem-second";

		[Fact]
		public async Task read_forwards_uses_the_reader_that_claims_the_stream()
		{
			var firstReader = new FakeVirtualStreamReader(FirstStream, lastEventNumber: 10);
			var secondReader = new FakeVirtualStreamReader(SecondStream, lastEventNumber: 20);
			var sut = new VirtualStreamReader([firstReader, secondReader]);

			var result = await sut.ReadForwards(
				GenReadForwards(Guid.NewGuid(), fromEventNumber: 0, maxCount: 1, SecondStream),
				CancellationToken.None);

			Assert.Equal(ReadStreamResult.Success, result.Result);
			Assert.Equal(20, result.LastEventNumber);
			Assert.Equal(0, firstReader.ForwardReadCount);
			Assert.Equal(1, secondReader.ForwardReadCount);
		}

		[Fact]
		public void metadata_uses_the_reader_that_claims_the_stream()
		{
			var firstReader = new FakeVirtualStreamReader(FirstStream, lastEventNumber: 10, lastIndexedPosition: 100);
			var secondReader = new FakeVirtualStreamReader(SecondStream, lastEventNumber: 20, lastIndexedPosition: 200);
			var sut = new VirtualStreamReader([firstReader, secondReader]);

			Assert.Equal(20, sut.GetLastEventNumber(SecondStream));
			Assert.Equal(200, sut.GetLastIndexedPosition(SecondStream));
		}

		[Fact]
		public async Task earlier_reader_wins_when_more_than_one_reader_claims_the_stream()
		{
			var firstReader = new FakeVirtualStreamReader(FirstStream, lastEventNumber: 10);
			var secondReader = new FakeVirtualStreamReader(FirstStream, lastEventNumber: 20);
			var sut = new VirtualStreamReader([firstReader, secondReader]);

			var result = await sut.ReadForwards(
				GenReadForwards(Guid.NewGuid(), fromEventNumber: 0, maxCount: 1, FirstStream),
				CancellationToken.None);

			Assert.Equal(10, result.LastEventNumber);
			Assert.Equal(1, firstReader.ForwardReadCount);
			Assert.Equal(0, secondReader.ForwardReadCount);
		}
	}

	private sealed class FakeVirtualStreamReader(
		string streamId,
		long lastEventNumber,
		long lastIndexedPosition = -1) : IVirtualStreamReader
	{
		public int ForwardReadCount { get; private set; }

		public ValueTask<ClientMessage.ReadStreamEventsForwardCompleted> ReadForwards(
			ClientMessage.ReadStreamEventsForward msg,
			CancellationToken token)
		{
			ForwardReadCount++;
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
				nextEventNumber: lastEventNumber + 1,
				lastEventNumber: lastEventNumber,
				isEndOfStream: true,
				tfLastCommitPosition: lastIndexedPosition));
		}

		public ValueTask<ClientMessage.ReadStreamEventsBackwardCompleted> ReadBackwards(
			ClientMessage.ReadStreamEventsBackward msg,
			CancellationToken token) =>
			ValueTask.FromResult(new ClientMessage.ReadStreamEventsBackwardCompleted(
				msg.CorrelationId,
				msg.EventStreamId,
				msg.FromEventNumber,
				msg.MaxCount,
				ReadStreamResult.Success,
				Array.Empty<ResolvedEvent>(),
				StreamMetadata.Empty,
				isCachePublic: false,
				error: string.Empty,
				nextEventNumber: -1,
				lastEventNumber: lastEventNumber,
				isEndOfStream: true,
				tfLastCommitPosition: lastIndexedPosition));

		public long GetLastEventNumber(string streamId) => lastEventNumber;

		public long GetLastIndexedPosition(string streamId) => lastIndexedPosition;

		public bool CanReadStream(string candidate) => candidate == streamId;
	}
}
