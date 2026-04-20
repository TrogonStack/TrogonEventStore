using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messaging;
using EventStore.Core.Tests.Bus.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Bus;

[TestFixture]
public abstract class when_stopping_queued_handler : QueuedHandlerTestWithNoopConsumer
{
	protected when_stopping_queued_handler(
		Func<IHandle<Message>, string, TimeSpan, IQueuedHandler> queuedHandlerFactory)
		: base(queuedHandlerFactory)
	{
	}


	[Test]
	public async Task gracefully_should_not_throw()
	{
		_ = Queue.Start();
		await Queue.Stop();
	}

	[Test]
	public async Task gracefully_and_queue_is_not_busy_should_not_take_much_time()
	{
		_ = Queue.Start();
		await Queue.Stop().WaitAsync(TimeSpan.FromMilliseconds(5000));
	}

	[Test]
	public async Task second_time_should_not_throw()
	{
		_ = Queue.Start();
		await Queue.Stop();
		await Queue.Stop();
	}

	[Test]
	public async Task second_time_should_not_take_much_time()
	{
		_ = Queue.Start();
		await Queue.Stop();
		var secondStop = Queue.Stop();
		await secondStop.WaitAsync(TimeSpan.FromMilliseconds(1000));
		Assert.That(secondStop.IsCompletedSuccessfully, Is.True);
	}

	[Test]
	public void while_queue_is_busy_should_crash_with_timeout()
	{
		var consumer = new WaitingConsumer(1);
		var busyQueue = new QueuedHandlerThreadPool(consumer, "busy_test_queue", new QueueStatsManager(), new(),
			watchSlowMsg: false,
			threadStopWaitTimeout: TimeSpan.FromMilliseconds(100));
		var waitHandle = new ManualResetEvent(false);
		var handledEvent = new ManualResetEvent(false);
		try
		{
			busyQueue.Start();
			busyQueue.Publish(new DeferredExecutionTestMessage(() =>
			{
				handledEvent.Set();
				waitHandle.WaitOne();
			}));

			handledEvent.WaitOne();
			Assert.ThrowsAsync<TimeoutException>(async () => await busyQueue.Stop());
		}
		finally
		{
			waitHandle.Set();
			consumer.Wait();

			busyQueue.RequestStop();
			waitHandle.Dispose();
			handledEvent.Dispose();
			consumer.Dispose();
		}
	}

	[Test]
	public async Task while_processing_message_cancelled_by_queue_stop_should_not_timeout()
	{
		var started = new ManualResetEventSlim(false);
		var cancelled = new ManualResetEventSlim(false);
		var queue = new QueuedHandlerThreadPool(
			new AdHocHandler<Message>(async (_, token) =>
			{
				started.Set();

				try
				{
					await Task.Delay(Timeout.Infinite, token);
				}
				catch (OperationCanceledException ex) when (ex.CancellationToken == token)
				{
					cancelled.Set();
					throw;
				}
			}),
			"cancelled_test_queue",
			new QueueStatsManager(),
			new(),
			watchSlowMsg: false,
			threadStopWaitTimeout: TimeSpan.FromMilliseconds(500));

		try
		{
			var startTask = queue.Start();
			queue.Publish(new TestMessage());

			Assert.IsTrue(started.Wait(5000), "Consumer never started handling the message.");
			await queue.Stop();
			Assert.IsTrue(cancelled.Wait(5000), "Consumer never observed queue cancellation.");
			Assert.That(startTask.IsCompletedSuccessfully, Is.True,
				"Queue lifecycle task should complete successfully after a graceful stop.");
		}
		finally
		{
			queue.RequestStop();

			started.Dispose();
			cancelled.Dispose();
		}
	}

	[Test]
	public async Task while_processing_message_cancelled_by_message_token_should_continue_processing_queue()
	{
		var started = new ManualResetEventSlim(false);
		var cancelled = new ManualResetEventSlim(false);
		var processedNext = new ManualResetEventSlim(false);
		using var cancellationTokenSource = new CancellationTokenSource();
		var queue = new QueuedHandlerThreadPool(
			new AdHocHandler<Message>(async (message, token) =>
			{
				if (message is CancelledMessage)
				{
					started.Set();
					try
					{
						await Task.Delay(Timeout.Infinite, message.CancellationToken);
					}
					catch (OperationCanceledException ex) when (ex.CancellationToken == message.CancellationToken)
					{
						cancelled.Set();
						throw;
					}

					return;
				}

				processedNext.Set();
			}),
			"message_cancelled_test_queue",
			new QueueStatsManager(),
			new(),
			watchSlowMsg: false,
			threadStopWaitTimeout: TimeSpan.FromMilliseconds(500));

		try
		{
			var startTask = queue.Start();
			queue.Publish(new CancelledMessage(cancellationTokenSource.Token));

			Assert.IsTrue(started.Wait(5000), "Consumer never started handling the cancelled message.");

			cancellationTokenSource.Cancel();
			Assert.IsTrue(cancelled.Wait(5000), "Consumer never observed message cancellation.");
			Assert.IsTrue(SpinWait.SpinUntil(() =>
			{
				var stats = queue.GetStatistics();
				return stats.CurrentIdleTime is not null
				       && stats.InProgressMessageType is null
				       && stats.TotalItemsProcessed == 0;
			}, 5000), "Queue stats never cleared the cancelled message state.");

			queue.Publish(new TestMessage());

			Assert.IsTrue(processedNext.Wait(5000), "Queue never resumed processing after message cancellation.");
			Assert.IsTrue(SpinWait.SpinUntil(() => queue.GetStatistics().TotalItemsProcessed == 1, 5000),
				"Queue stats never counted the next completed message.");
			Assert.That(startTask.IsCompleted, Is.False, "Message cancellation should not complete the queue lifecycle task.");
		}
		finally
		{
			cancellationTokenSource.Cancel();
			await queue.Stop();
			started.Dispose();
			cancelled.Dispose();
			processedNext.Dispose();
		}
	}

	[Test]
	public void default_message_token_should_not_match_unrelated_cancellation()
	{
		using var lifetimeSource = new CancellationTokenSource();
		var lifetimeToken = lifetimeSource.Token;
		var result = IsExpectedCancellation(new OperationCanceledException(), new TestMessage(), lifetimeToken);

		Assert.That(result, Is.False);
	}

	[Test]
	public void explicit_message_token_should_match_its_own_cancellation()
	{
		using var cancellationTokenSource = new CancellationTokenSource();
		using var lifetimeSource = new CancellationTokenSource();
		var result = IsExpectedCancellation(
			new OperationCanceledException(cancellationTokenSource.Token),
			new CancelledMessage(cancellationTokenSource.Token),
			lifetimeSource.Token);

		Assert.That(result, Is.True);
	}

	private static bool IsExpectedCancellation(
		OperationCanceledException exception,
		Message message,
		CancellationToken lifetimeToken) =>
		(bool)typeof(QueuedHandlerThreadPool)
			.GetMethod(
				"IsExpectedCancellation",
				BindingFlags.NonPublic | BindingFlags.Static)!
			.Invoke(null, [exception, message, lifetimeToken])!;
}

file sealed class CancelledMessage : Message
{
	public CancelledMessage(CancellationToken cancellationToken) : base(cancellationToken)
	{
	}
}

[TestFixture]
public class when_stopping_queued_handler_threadpool : when_stopping_queued_handler
{
	public when_stopping_queued_handler_threadpool()
		: base((consumer, name, timeout) =>
			new QueuedHandlerThreadPool(consumer, name, new QueueStatsManager(), new(), false, null, timeout))
	{
	}
}
