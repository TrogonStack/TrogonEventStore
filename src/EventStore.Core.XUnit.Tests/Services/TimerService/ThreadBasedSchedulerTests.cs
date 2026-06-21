using System;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Metrics;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Time;
using Xunit;

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

	private static ThreadBasedScheduler CreateScheduler(IClock clock = null) =>
		new(new QueueStatsManager(), new QueueTrackers(), clock);

	private sealed class FrozenClock : IClock
	{
		public Instant Now => Instant.FromSeconds(1);

		public long SecondsSinceEpoch => 1;
	}
}
