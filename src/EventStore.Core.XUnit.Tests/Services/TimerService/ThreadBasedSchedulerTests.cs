using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Metrics;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Time;
using Xunit;
using Conf = EventStore.Common.Configuration.MetricsConfiguration;

namespace EventStore.Core.XUnit.Tests.Services.TimerService;

public class ThreadBasedSchedulerTests
{
	[Fact]
	public async Task task_completes_when_stopped()
	{
		using var scheduler = CreateScheduler();

		scheduler.Stop();

		await scheduler.Task.WaitAsync(TimeSpan.FromSeconds(1));
	}

	[Fact]
	public async Task future_work_does_not_keep_shutdown_incomplete()
	{
		using var scheduler = CreateScheduler();

		scheduler.Schedule(TimeSpan.FromMinutes(1), static (_, _) => { }, null);
		scheduler.Stop();

		await scheduler.Task.WaitAsync(TimeSpan.FromSeconds(1));
	}

	[Fact]
	public async Task statistics_include_scheduled_work()
	{
		using var scheduler = CreateScheduler(new FrozenClock());

		scheduler.Schedule(TimeSpan.FromMinutes(1), static (_, _) => { }, null);
		scheduler.Schedule(TimeSpan.FromMinutes(2), static (_, _) => { }, null);

		await AssertEventually(() => scheduler.GetStatistics().Length == 2);
		scheduler.Stop();

		await scheduler.Task.WaitAsync(TimeSpan.FromSeconds(1));
	}

	[Fact]
	public async Task callback_failure_does_not_fault_scheduler_lifetime()
	{
		using var scheduler = CreateScheduler();
		var failedCallbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var nextCallbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		scheduler.Schedule(TimeSpan.Zero, static (_, state) =>
		{
			((TaskCompletionSource)state).SetResult();
			throw new InvalidOperationException("boom");
		}, failedCallbackRan);

		await failedCallbackRan.Task.WaitAsync(TimeSpan.FromSeconds(1));

		scheduler.Schedule(TimeSpan.Zero, static (_, state) =>
		{
			((TaskCompletionSource)state).SetResult();
		}, nextCallbackRan);

		await nextCallbackRan.Task.WaitAsync(TimeSpan.FromSeconds(1));
		scheduler.Stop();

		await scheduler.Task.WaitAsync(TimeSpan.FromSeconds(1));
	}

	[Fact]
	public async Task callback_failure_is_counted_as_processed_work()
	{
		using var scheduler = CreateScheduler();
		var failedCallbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var nextCallbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		scheduler.Schedule(TimeSpan.Zero, static (_, state) =>
		{
			((TaskCompletionSource)state).SetResult();
			throw new InvalidOperationException("boom");
		}, failedCallbackRan);
		scheduler.Schedule(TimeSpan.Zero, static (_, state) =>
		{
			((TaskCompletionSource)state).SetResult();
		}, nextCallbackRan);

		await Task.WhenAll(failedCallbackRan.Task, nextCallbackRan.Task).WaitAsync(TimeSpan.FromSeconds(1));

		await AssertEventually(() => scheduler.GetStatistics().TotalItemsProcessed >= 4);
	}

	[Fact]
	public async Task busy_tracker_observes_scheduler_work_and_idle()
	{
		var tracker = new RecordingTracker();
		using var scheduler = CreateScheduler(trackers: CreateTrackers(tracker));
		var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var busyBefore = tracker.BusyCount;
		var idleBefore = tracker.IdleCount;

		scheduler.Schedule(TimeSpan.Zero, static (_, state) =>
		{
			((TaskCompletionSource)state).SetResult();
		}, callbackRan);

		await callbackRan.Task.WaitAsync(TimeSpan.FromSeconds(1));

		await AssertEventually(() => tracker.BusyCount > busyBefore && tracker.IdleCount > idleBefore);
	}

	private static async Task AssertEventually(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(1);

		while (!condition())
		{
			if (DateTime.UtcNow >= deadline)
			{
				Assert.True(condition());
			}

			await Task.Delay(10);
		}
	}

	private static ThreadBasedScheduler CreateScheduler(IClock clock = null, QueueTrackers trackers = null) =>
		new(new QueueStatsManager(), trackers ?? new QueueTrackers(), clock);

	private static QueueTrackers CreateTrackers(RecordingTracker tracker) =>
		new(
			new[]
			{
				new Conf.LabelMappingCase
				{
					Regex = "Timer",
					Label = "Timer"
				}
			},
			_ => tracker,
			_ => tracker,
			_ => tracker,
			_ => tracker);

	private sealed class FrozenClock : IClock
	{
		public Instant Now => Instant.FromSeconds(1);

		public long SecondsSinceEpoch => 1;
	}

	private sealed class RecordingTracker :
		IQueueBusyTracker,
		IQueueLengthTracker,
		IDurationMaxTracker,
		IQueueProcessingTracker
	{
		private int _busyCount;
		private int _idleCount;

		public int BusyCount => Volatile.Read(ref _busyCount);

		public int IdleCount => Volatile.Read(ref _idleCount);

		public void EnterBusy()
		{
			Interlocked.Increment(ref _busyCount);
		}

		public void EnterIdle()
		{
			Interlocked.Increment(ref _idleCount);
		}

		public Instant RecordNow(Instant start) => start;

		public Instant RecordNow(Instant start, string messageType) => start;

		public void SetQueueLength(int length)
		{
		}
	}
}
