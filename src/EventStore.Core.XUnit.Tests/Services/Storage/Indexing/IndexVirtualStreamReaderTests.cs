using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Messages;
using EventStore.Core.Services.Storage.Indexing;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class IndexVirtualStreamReaderTests
{
	[Fact]
	public void constructor_rejects_missing_stream_id()
	{
		var exception = Assert.Throws<ArgumentNullException>(() => new TestIndexVirtualStreamReader(null!));

		Assert.Equal("streamId", exception.ParamName);
	}

	[Fact]
	public void exposes_stream_id()
	{
		var streamId = new IndexStreamId("$index-stream");
		var reader = new TestIndexVirtualStreamReader(streamId);

		Assert.Same(streamId, reader.StreamId);
	}

	[Fact]
	public void can_read_stream_when_candidate_matches()
	{
		var reader = new TestIndexVirtualStreamReader(new IndexStreamId("$index-stream"));

		Assert.True(reader.CanReadStream("$index-stream"));
	}

	[Fact]
	public void cannot_read_stream_when_candidate_differs()
	{
		var reader = new TestIndexVirtualStreamReader(new IndexStreamId("$index-stream"));

		Assert.False(reader.CanReadStream("other-stream"));
	}

	[Fact]
	public void cannot_read_stream_when_candidate_is_null()
	{
		var reader = new TestIndexVirtualStreamReader(new IndexStreamId("$index-stream"));

		Assert.False(reader.CanReadStream(null!));
	}

	private sealed class TestIndexVirtualStreamReader(IndexStreamId streamId) : IndexVirtualStreamReader(streamId)
	{
		public override ValueTask<ClientMessage.ReadStreamEventsForwardCompleted> ReadForwards(
			ClientMessage.ReadStreamEventsForward msg,
			CancellationToken token) =>
			throw new NotSupportedException();

		public override ValueTask<ClientMessage.ReadStreamEventsBackwardCompleted> ReadBackwards(
			ClientMessage.ReadStreamEventsBackward msg,
			CancellationToken token) =>
			throw new NotSupportedException();

		public override long GetLastEventNumber(string streamId) => throw new NotSupportedException();

		public override long GetLastIndexedPosition(string streamId) => throw new NotSupportedException();
	}
}
