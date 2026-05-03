using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using EventStore.ClientAPI.SystemData;
using EventStore.Core.Services;
using EventStore.Core.Tests.ClientAPI.Helpers;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.ClientAPI;

[Category("LongRunning"), Category("ClientAPI"), NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class catchup_filtered_subscription<TLogFormat, TStreamId> : SpecificationWithDirectory
{
	private MiniNode<TLogFormat, TStreamId> _node;
	private IEventStoreConnection _conn;
	private List<EventData> _testEvents;
	private List<EventData> _testEventsAfter;
	private const int Timeout = 10000;
	private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(10);
	private static readonly TimeSpan ConnectionCloseTimeout = TimeSpan.FromSeconds(10);
	private const int LongRunningTimeout = 600000;

	[SetUp]
	public override async Task SetUp()
	{
		await base.SetUp();
		_node = new MiniNode<TLogFormat, TStreamId>(PathName);
		await _node.Start(StartupTimeout);
		await _node.WaitForTcpEndPoint().WithTimeout(TimeSpan.FromSeconds(60));

		_conn = await TestConnectionLifecycle.ReconnectUntilReady(
			() => BuildConnection(_node),
			conn => conn.ReadAllEventsForwardAsync(Position.Start, 1, false, DefaultData.AdminCredentials),
			StartupTimeout);
		await _conn.SetStreamMetadataAsync(SystemStreams.AllStream, -1,
			StreamMetadata.Build().SetReadRole(SystemRoles.All),
			new UserCredentials(SystemUsers.Admin, SystemUsers.DefaultAdminPassword));

		_testEvents = Enumerable
			.Range(0, 10)
			.Select(x => TestEvent.NewTestEvent(x.ToString(), eventName: "AEvent"))
			.ToList();

		_testEvents.AddRange(
			Enumerable
				.Range(0, 10)
				.Select(x => TestEvent.NewTestEvent(x.ToString(), eventName: "BEvent")).ToList());

		_testEventsAfter = Enumerable
			.Range(0, 10)
			.Select(x => TestEvent.NewTestEvent(x.ToString(), eventName: "AEvent"))
			.ToList();

		_testEventsAfter.AddRange(
			Enumerable
				.Range(0, 10)
				.Select(x => TestEvent.NewTestEvent(x.ToString(), eventName: "BEvent")).ToList());

		await _conn.AppendToStreamAsync("stream-a", ExpectedVersion.NoStream, _testEvents.EvenEvents());
		await _conn.AppendToStreamAsync("stream-b", ExpectedVersion.NoStream, _testEvents.OddEvents());
	}

	protected virtual IEventStoreConnection BuildConnection(MiniNode<TLogFormat, TStreamId> node)
	{
		return TestConnection.Create(node.TcpEndPoint);
	}

	[Test]
	public void calls_checkpoint_delegate_during_catchup()
	{
		var filter = Filter.StreamId.Prefix("stream-a");
		// in v2 there are 20 events, 10 in stream-a and 10 in stream-b.
		// in v3 there are additionally two stream records and two event type records
		var isV2 = LogFormatHelper<TLogFormat, TStreamId>.IsV2;
		var checkpointReached = new CountdownEvent(isV2 ? 10 : 14);
		var eventsSeen = 0;

		var settings = new CatchUpSubscriptionFilteredSettings(
			maxLiveQueueSize: 10000,
			readBatchSize: 2,
			verboseLogging: false,
			resolveLinkTos: true,
			maxSearchWindow: 2,
			subscriptionName: String.Empty
		);

		var subscription = _conn.FilteredSubscribeToAllFrom(
			Position.Start,
			filter,
			settings,
			(s, e) =>
			{
				eventsSeen++;
				return Task.CompletedTask;
			},
			(s, p) =>
			{
				checkpointReached.Signal();
				return Task.CompletedTask;
			}, 1);

		try
		{
			if (!checkpointReached.Wait(Timeout))
			{
				Assert.Fail("Checkpoint reached not called enough times within time limit.");
			}

			Assert.AreEqual(10, eventsSeen);
		}
		finally
		{
			StopSubscription(subscription);
		}
	}

	[Test]
	public void calls_checkpoint_during_live_processing_stage()
	{
		var filter = Filter.StreamId.Prefix("stream-a");
		var appeared = new CountdownEvent(_testEventsAfter.EvenEvents().Count + 1); // Calls once for switch to live.
		var eventsSeen = 0;
		var isLive = false;

		var settings = new CatchUpSubscriptionFilteredSettings(
			10000,
			1,
			verboseLogging: false,
			resolveLinkTos: true,
			maxSearchWindow: 1,
			subscriptionName: String.Empty
		);

		var subscription = _conn.FilteredSubscribeToAllFrom(
			Position.Start,
			filter,
			settings,
			(s, e) =>
			{
				eventsSeen++;
				return Task.CompletedTask;
			},
			(s, p) =>
			{
				if (isLive)
				{
					appeared.Signal();
				}

				return Task.CompletedTask;
			}, 1, s =>
			{
				isLive = true;
				_conn.AppendToStreamAsync("stream-a", ExpectedVersion.Any, _testEventsAfter.EvenEvents()).Wait();
			});

		try
		{
			if (!appeared.Wait(Timeout))
			{
				Assert.Fail("Checkpoint appeared not called enough times within time limit.");
			}

			Assert.AreEqual(20, eventsSeen);
		}
		finally
		{
			StopSubscription(subscription);
		}
	}

	[Test, Category("LongRunning"), Timeout(LongRunningTimeout)]
	public void only_return_events_with_a_given_stream_prefix()
	{
		var filter = Filter.StreamId.Prefix("stream-a");
		var foundEvents = new ConcurrentBag<ResolvedEvent>();
		var appeared = new CountdownEvent(20);
		var subscription = Subscribe(filter, foundEvents, appeared);
		try
		{
			if (!appeared.Wait(Timeout))
			{
				Assert.Fail("Appeared countdown event timed out.");
			}

			Assert.True(foundEvents.All(e => e.Event.EventStreamId == "stream-a"));
		}
		finally
		{
			StopSubscription(subscription);
		}
	}

	[Test, Category("LongRunning"), Timeout(LongRunningTimeout)]
	public void only_return_events_with_a_given_event_prefix()
	{
		var filter = Filter.EventType.Prefix("AE");
		var foundEvents = new ConcurrentBag<ResolvedEvent>();
		var appeared = new CountdownEvent(20);
		var subscription = Subscribe(filter, foundEvents, appeared);
		try
		{
			if (!appeared.Wait(Timeout))
			{
				Assert.Fail("Appeared countdown event timed out.");
			}

			Assert.True(foundEvents.All(e => e.Event.EventType == "AEvent"));
		}
		finally
		{
			StopSubscription(subscription);
		}
	}

	[Test, Category("LongRunning"), Timeout(LongRunningTimeout)]
	public void only_return_events_that_satisfy_a_given_stream_regex()
	{
		var filter = Filter.StreamId.Regex(new Regex(@"^.*eam-b.*$"));
		var foundEvents = new ConcurrentBag<ResolvedEvent>();
		var appeared = new CountdownEvent(20);
		var subscription = Subscribe(filter, foundEvents, appeared);
		try
		{
			if (!appeared.Wait(Timeout))
			{
				Assert.Fail("Appeared countdown event timed out.");
			}

			Assert.True(foundEvents.All(e => e.Event.EventStreamId == "stream-b"));
		}
		finally
		{
			StopSubscription(subscription);
		}
	}

	[Test, Category("LongRunning"), Timeout(LongRunningTimeout)]
	public void only_return_events_that_satisfy_a_given_event_regex()
	{
		var filter = Filter.EventType.Regex(new Regex(@"^.*BEv.*$"));
		var foundEvents = new ConcurrentBag<ResolvedEvent>();
		var appeared = new CountdownEvent(20);
		var subscription = Subscribe(filter, foundEvents, appeared);
		try
		{
			if (!appeared.Wait(Timeout))
			{
				Assert.Fail("Appeared countdown event timed out.");
			}

			Assert.True(foundEvents.All(e => e.Event.EventType == "BEvent"));
		}
		finally
		{
			StopSubscription(subscription);
		}
	}

	[Test, Category("LongRunning"), Timeout(LongRunningTimeout)]
	public void only_return_events_that_are_not_system_events()
	{
		var filter = Filter.ExcludeSystemEvents;
		var foundEvents = new ConcurrentBag<ResolvedEvent>();
		using var appeared = new ManualResetEventSlim();
		const int requiredEvents = 20;
		var foundEventsCount = 0;
		var subscription = _conn.FilteredSubscribeToAllFrom(
			Position.Start,
			filter,
			CatchUpSubscriptionFilteredSettings.Default,
			(s, e) =>
			{
				foundEvents.Add(e);
				if (Interlocked.Increment(ref foundEventsCount) >= requiredEvents)
				{
					appeared.Set();
				}

				return Task.CompletedTask;
			},
			(s, p) => Task.CompletedTask, 5,
			s =>
			{
				_conn.AppendToStreamAsync("stream-a", ExpectedVersion.Any, _testEventsAfter.EvenEvents()).Wait();
				_conn.AppendToStreamAsync("stream-b", ExpectedVersion.Any, _testEventsAfter.OddEvents()).Wait();
			});
		try
		{
			if (!appeared.Wait(Timeout))
			{
				Assert.Fail("Appeared countdown event timed out.");
			}

			Assert.True(foundEvents.All(e => !e.Event.EventType.StartsWith("$")));
		}
		finally
		{
			StopSubscription(subscription);
		}
	}

	private EventStoreCatchUpSubscription Subscribe(Filter filter, ConcurrentBag<ResolvedEvent> foundEvents,
		CountdownEvent appeared)
	{
		return _conn.FilteredSubscribeToAllFrom(
			Position.Start,
			filter,
			CatchUpSubscriptionFilteredSettings.Default,
			(s, e) =>
			{
				foundEvents.Add(e);
				appeared.Signal();
				return Task.CompletedTask;
			},
			(s, p) => Task.CompletedTask, 5,
			s =>
			{
				_conn.AppendToStreamAsync("stream-a", ExpectedVersion.Any, _testEventsAfter.EvenEvents()).Wait();
				_conn.AppendToStreamAsync("stream-b", ExpectedVersion.Any, _testEventsAfter.OddEvents()).Wait();
			});
	}

	[TearDown]
	public override async Task TearDown()
	{
		try
		{
			await TestConnectionLifecycle.CloseConnectionAndWait(_conn, ConnectionCloseTimeout);
		}
		catch
		{
			TestConnectionLifecycle.TryCloseConnection(_conn);
		}

		TestConnectionLifecycle.DisposeIfNeeded(_conn);
		await _node.Shutdown();
		await base.TearDown();
	}

	private static void StopSubscription(EventStoreCatchUpSubscription subscription)
	{
		try
		{
			subscription.Stop(ConnectionCloseTimeout);
		}
		catch
		{
		}
	}
}
