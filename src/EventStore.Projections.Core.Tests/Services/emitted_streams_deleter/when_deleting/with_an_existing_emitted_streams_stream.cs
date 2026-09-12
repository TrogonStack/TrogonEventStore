using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Tests;
using EventStore.Projections.Core.Services.Processing;
using EventStore.Projections.Core.Services.Processing.Checkpointing;
using EventStore.Projections.Core.Services.Processing.Emitting;
using EventStore.Projections.Core.Services.Processing.Emitting.EmittedEvents;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.emitted_streams_deleter.when_deleting;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class with_an_existing_emitted_streams_stream<TLogFormat, TStreamId> : SpecificationWithEmittedStreamsTrackerAndDeleter<TLogFormat, TStreamId>
{
	protected Action _onDeleteStreamCompleted;
	protected ManualResetEvent _resetEvent = new ManualResetEvent(false);
	private string _testStreamName = "test_stream";
	private ManualResetEvent _eventAppeared = new ManualResetEvent(false);

	protected override async Task Given()
	{
		_onDeleteStreamCompleted = () => { _resetEvent.Set(); };

		await base.Given();

		_emittedStreamsTracker.TrackEmittedStream(new EmittedEvent[] {
			new EmittedDataEvent(
				_testStreamName, Guid.NewGuid(), "type1", true,
				"data", null, CheckpointTag.FromPosition(0, 100, 50), null),
		});

		var events = await WaitForEvents(_projectionNamesBuilder.GetEmittedStreamsName(), 1);
		if (events.Length != 1)
		{
			Assert.Fail("Timed out waiting for emitted stream event");
		}
		_eventAppeared.Set();
	}

	protected override Task When()
	{
		_emittedStreamsDeleter.DeleteEmittedStreams(_onDeleteStreamCompleted);
		if (!_resetEvent.WaitOne(TimeSpan.FromSeconds(10)))
		{
			throw new Exception("Timed out waiting callback.");
		}

		return Task.CompletedTask;
	}

	[Test]
	public async Task should_have_deleted_the_tracked_emitted_stream()
	{
		var events = await ReadEvents(_testStreamName, 1);
		Assert.AreEqual(0, events.Length);
	}


	[Test]
	public async Task should_have_deleted_the_checkpoint_stream()
	{
		var events = await ReadEvents(_projectionNamesBuilder.GetEmittedStreamsCheckpointName(), 1);
		Assert.AreEqual(0, events.Length);
	}

	[Test]
	public async Task should_have_deleted_the_emitted_streams_stream()
	{
		var events = await ReadEvents(_projectionNamesBuilder.GetEmittedStreamsName(), 1);
		Assert.AreEqual(0, events.Length);
	}
}
