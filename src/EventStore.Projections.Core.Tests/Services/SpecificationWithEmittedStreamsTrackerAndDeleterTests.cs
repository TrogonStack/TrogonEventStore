using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Tests;
using Grpc.Core;
using NUnit.Framework;
using StreamsClient = EventStore.Client.Streams.Streams.StreamsClient;

namespace EventStore.Projections.Core.Tests.Services;

[TestFixture]
public class SpecificationWithEmittedStreamsTrackerAndDeleterTests
{
	[Test]
	public void read_events_cancels_the_stream_when_the_helper_times_out()
	{
		var client = new BlockingStreamsClient();
		var specification = new TestSpecification(client);

		Assert.ThrowsAsync<TaskCanceledException>(async () =>
			await specification.ReadEventCount("stream", 1));
		Assert.That(client.CallCancellationToken.CanBeCanceled, Is.True);
		Assert.That(client.CallCancellationToken.IsCancellationRequested, Is.True);
		Assert.That(client.Reader.MoveNextCancellationToken,
			Is.EqualTo(client.CallCancellationToken));
	}

	[Test]
	public async Task wait_for_events_returns_the_last_result_when_the_helper_times_out()
	{
		var client = new BlockingStreamsClient();
		var specification = new TestSpecification(client);

		var eventCount = await specification.WaitForEventCount("stream", 1);

		Assert.That(eventCount, Is.Zero);
		Assert.That(client.CallCancellationToken.CanBeCanceled, Is.True);
		Assert.That(client.CallCancellationToken.IsCancellationRequested, Is.True);
		Assert.That(client.Reader.MoveNextCancellationToken,
			Is.EqualTo(client.CallCancellationToken));
	}

	private sealed class TestSpecification
		: SpecificationWithEmittedStreamsTrackerAndDeleter<LogFormat.V2, string>
	{
		public TestSpecification(StreamsClient client)
		{
			_client = client;
		}

		protected override TimeSpan Timeout => TimeSpan.FromMilliseconds(25);

		protected override Task When() => Task.CompletedTask;

		public async Task<int> ReadEventCount(string stream, int count) =>
			(await ReadEvents(stream, count)).Events.Length;

		public async Task<int> WaitForEventCount(string stream, int count) =>
			(await WaitForEvents(stream, count)).Length;
	}

	private sealed class BlockingStreamsClient : StreamsClient
	{
		public BlockingStreamReader Reader { get; } = new();
		public CancellationToken CallCancellationToken { get; private set; }

		public override AsyncServerStreamingCall<ReadResp> Read(ReadReq request, CallOptions options)
		{
			CallCancellationToken = options.CancellationToken;
			return new AsyncServerStreamingCall<ReadResp>(
				Reader,
				Task.FromResult(new Metadata()),
				() => Status.DefaultSuccess,
				() => new Metadata(),
				() => { });
		}
	}

	private sealed class BlockingStreamReader : IAsyncStreamReader<ReadResp>
	{
		public ReadResp Current => null;
		public CancellationToken MoveNextCancellationToken { get; private set; }

		public Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			MoveNextCancellationToken = cancellationToken;
			return cancellationToken.CanBeCanceled
				? WaitForCancellation(cancellationToken)
				: Task.FromResult(false);
		}

		private static async Task<bool> WaitForCancellation(CancellationToken cancellationToken)
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return false;
		}
	}
}
