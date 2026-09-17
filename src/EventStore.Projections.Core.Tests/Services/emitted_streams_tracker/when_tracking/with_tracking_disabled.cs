using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Tests;
using EventStore.Projections.Core.Services.Processing;
using EventStore.Projections.Core.Services.Processing.Checkpointing;
using EventStore.Projections.Core.Services.Processing.Emitting;
using EventStore.Projections.Core.Services.Processing.Emitting.EmittedEvents;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.emitted_streams_tracker.when_tracking;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class with_tracking_disabled<TLogFormat, TStreamId> : SpecificationWithEmittedStreamsTrackerAndDeleter<TLogFormat, TStreamId>
{
	private CountdownEvent _eventAppeared = new CountdownEvent(1);

	protected override TimeSpan Timeout { get; } = TimeSpan.FromSeconds(10);

	protected override Task Given()
	{
		_trackEmittedStreams = false;
		return base.Given();
	}

	protected override async Task When()
	{
		using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		using var subscription = SubscribeToStream(
			_projectionNamesBuilder.GetEmittedStreamsName(),
			true,
			observation.Token);
		Assert.That(await subscription.ResponseStream.MoveNext(observation.Token), Is.True);
		Assert.That(
			subscription.ResponseStream.Current.ContentCase,
			Is.EqualTo(EventStore.Client.Streams.ReadResp.ContentOneofCase.Confirmation));

		_emittedStreamsTracker.TrackEmittedStream(new EmittedEvent[] {
			new EmittedDataEvent(
				"test_stream", Guid.NewGuid(), "type1", true,
				"data", null, CheckpointTag.FromPosition(0, 100, 50), null, null)
		});

		try
		{
			while (await subscription.ResponseStream.MoveNext(observation.Token))
			{
				if (subscription.ResponseStream.Current.ContentCase ==
					EventStore.Client.Streams.ReadResp.ContentOneofCase.Event)
				{
					_eventAppeared.Signal();
					break;
				}
			}
		}
		catch (OperationCanceledException) when (observation.IsCancellationRequested)
		{
		}
		catch (RpcException ex) when (observation.IsCancellationRequested &&
			ex.StatusCode is StatusCode.Cancelled or StatusCode.DeadlineExceeded)
		{
		}
	}

	[Test]
	public async Task should_write_a_stream_tracked_event()
	{
		var result = await ReadEvents(_projectionNamesBuilder.GetEmittedStreamsName(), 200);
		Assert.AreEqual(0, result.Events.Length);
		Assert.AreEqual(1, _eventAppeared.CurrentCount);
	}
}
