using System;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Metrics;
using EventStore.Core.Services.TimerService;
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

	private static ThreadBasedScheduler CreateScheduler() =>
		new(new QueueStatsManager(), new QueueTrackers());
}
