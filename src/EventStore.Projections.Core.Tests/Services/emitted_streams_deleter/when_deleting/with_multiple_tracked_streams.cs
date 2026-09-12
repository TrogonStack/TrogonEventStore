using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Tests;
using EventStore.Projections.Core.Services.Processing;
using EventStore.Projections.Core.Services.Processing.Checkpointing;
using EventStore.Projections.Core.Services.Processing.Emitting;
using EventStore.Projections.Core.Services.Processing.Emitting.EmittedEvents;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.emitted_streams_deleter.when_deleting;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class with_multiple_tracked_streams<TLogFormat, TStreamId> : SpecificationWithEmittedStreamsTrackerAndDeleter<TLogFormat, TStreamId>
{
	protected Action _onDeleteStreamCompleted;
	protected ManualResetEvent _resetEvent = new ManualResetEvent(false);
	protected CountdownEvent _eventAppeared;
	private int _numberOfTrackedEvents = 50;
	private string _testStreamFormat = "test_stream_{0}";

	protected override async Task Given()
	{
		_eventAppeared = new CountdownEvent(_numberOfTrackedEvents);
		_onDeleteStreamCompleted = () => { _resetEvent.Set(); };
		await base.Given();

		for (int i = 0; i < _numberOfTrackedEvents; i++)
		{
			await AppendEvent(String.Format(_testStreamFormat, i), "type1", Helper.UTF8NoBom.GetBytes("data"));
			_emittedStreamsTracker.TrackEmittedStream(new EmittedEvent[] {
				new EmittedDataEvent(
					String.Format(_testStreamFormat, i), Guid.NewGuid(), "type1", true,
					"data", null, CheckpointTag.FromPosition(0, 100, 50), null),
			});
		}

		var events = await WaitForEvents(_projectionNamesBuilder.GetEmittedStreamsName(), _numberOfTrackedEvents);
		if (events.Length != _numberOfTrackedEvents)
		{
			Assert.Fail("Timed out waiting for emitted streams");
		}
		while (_eventAppeared.CurrentCount > 0)
			_eventAppeared.Signal();
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
	public async Task should_have_deleted_the_tracked_emitted_streams()
	{
		for (int i = 0; i < _numberOfTrackedEvents; i++)
		{
			var events = await ReadEvents(String.Format(_testStreamFormat, i), 1);
			Assert.AreEqual(0, events.Length);
		}
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
