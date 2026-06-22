using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Metrics;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Time;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.TimeService;

[TestFixture]
public class ThreadBasedSchedulerTests
{
	private ThreadBasedScheduler _scheduler;

	[TearDown]
	public async Task TearDown()
	{
		if (_scheduler is null)
		{
			return;
		}

		_scheduler.Dispose();
		await _scheduler.Task.WaitAsync(TimeSpan.FromSeconds(5));
		_scheduler = null;
	}

	[Test]
	public async Task executes_due_callbacks()
	{
		using var called = new ManualResetEventSlim();
		var scheduler = CreateScheduler();

		scheduler.Schedule(TimeSpan.Zero, (_, _) => called.Set(), null);

		Assert.That(await WaitUntil(() => called.IsSet), Is.True);
	}

	[Test]
	public async Task continues_running_after_a_callback_fails()
	{
		using var called = new ManualResetEventSlim();
		var scheduler = CreateScheduler();

		scheduler.Schedule(TimeSpan.Zero, (_, _) => throw new InvalidOperationException("boom"), null);
		scheduler.Schedule(TimeSpan.Zero, (_, _) => called.Set(), null);

		Assert.That(await WaitUntil(() => called.IsSet), Is.True);
		Assert.That(scheduler.Task.IsCompleted, Is.False);
	}

	[Test]
	public async Task reports_future_callbacks_as_queued_work()
	{
		var clock = new TestClock();
		var scheduler = CreateScheduler(clock);

		scheduler.Schedule(TimeSpan.FromMinutes(1), (_, _) => { }, null);

		Assert.That(await WaitUntil(() => scheduler.GetStatistics().Length == 1), Is.True);
	}

	[Test]
	public async Task completes_lifetime_task_when_stopped_while_waiting_for_future_work()
	{
		var clock = new TestClock();
		var scheduler = CreateScheduler(clock);

		scheduler.Schedule(TimeSpan.FromMinutes(1), (_, _) => { }, null);
		scheduler.Dispose();

		await scheduler.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.That(scheduler.Task.IsCompletedSuccessfully, Is.True);
	}

	private ThreadBasedScheduler CreateScheduler(IClock clock = null)
	{
		_scheduler = new ThreadBasedScheduler(new QueueStatsManager(), new QueueTrackers(), clock);
		return _scheduler;
	}

	private static async Task<bool> WaitUntil(Func<bool> condition)
	{
		var timeoutAt = DateTime.UtcNow.AddSeconds(5);

		while (DateTime.UtcNow < timeoutAt)
		{
			if (condition())
			{
				return true;
			}

			await Task.Delay(10);
		}

		return condition();
	}

	private sealed class TestClock : IClock
	{
		private long _ticks = Instant.Now.Ticks;

		public Instant Now => new(Interlocked.Read(ref _ticks));

		public long SecondsSinceEpoch => 0;
	}
}
