using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messaging;
using EventStore.Core.Metrics;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Bus;

public class QueuedHandlerThreadPoolTests
{
	[Fact]
	public async Task processes_messages_serially()
	{
		const int messageCount = 8;
		var consumer = new RecordingConsumer(messageCount, TimeSpan.FromMilliseconds(20));
		var queue = new QueuedHandlerThreadPool(
			consumer,
			"serial-queue-test",
			new QueueStatsManager(),
			new QueueTrackers(),
			watchSlowMsg: false,
			threadStopWaitTimeout: TimeSpan.FromSeconds(5));

		_ = queue.Start();
		try
		{
			for (var i = 0; i < messageCount; i++)
			{
				queue.Publish(new TestMessage());
			}

			await consumer.WaitUntilComplete(TimeSpan.FromSeconds(5));

			Assert.Equal(1, consumer.MaxConcurrency);
			Assert.Equal(messageCount, consumer.HandledCount);
		}
		finally
		{
			await queue.Stop();
		}
	}

	[Fact]
	public async Task keeps_message_queued_until_processing_limiter_allows_it()
	{
		var limiter = new ControlledProcessingLimiter();
		var consumer = new RecordingConsumer(1, TimeSpan.Zero);
		var queue = CreateQueue(consumer, limiter);

		_ = queue.Start();
		try
		{
			queue.Publish(new TestMessage());

			await limiter.WaitForAcquireAttempt(TimeSpan.FromSeconds(5));

			Assert.Equal(1, queue.MessageCount);
			Assert.Equal(0, consumer.HandledCount);

			limiter.ReleaseOne();

			await consumer.WaitUntilComplete(TimeSpan.FromSeconds(5));

			Assert.Equal(0, queue.MessageCount);
			Assert.Equal(1, consumer.HandledCount);
		}
		finally
		{
			await queue.Stop();
		}
	}

	[Fact]
	public async Task cancelled_message_does_not_block_later_message_while_waiting_for_limiter()
	{
		var limiter = new ControlledProcessingLimiter();
		var consumer = new RecordingConsumer(1, TimeSpan.Zero);
		var queue = CreateQueue(consumer, limiter);
		using var cancellationSource = new CancellationTokenSource();

		_ = queue.Start();
		try
		{
			queue.Publish(new CancelledMessage(cancellationSource.Token));
			await limiter.WaitForAcquireAttempt(TimeSpan.FromSeconds(5));

			cancellationSource.Cancel();
			Assert.True(SpinWait.SpinUntil(() => queue.MessageCount == 0, TimeSpan.FromSeconds(5)));

			queue.Publish(new TestMessage());
			Assert.True(SpinWait.SpinUntil(() => limiter.AcquireAttempts >= 2, TimeSpan.FromSeconds(5)));

			Assert.Equal(1, queue.MessageCount);

			limiter.ReleaseOne();
			await consumer.WaitUntilComplete(TimeSpan.FromSeconds(5));

			Assert.Equal(0, queue.MessageCount);
			Assert.Equal(1, consumer.HandledCount);
		}
		finally
		{
			await queue.Stop();
		}
	}

	[Fact]
	public async Task messages_excluded_by_limiter_are_processed_without_waiting()
	{
		var limiter = new ControlledProcessingLimiter(message => message is not UnthrottledMessage);
		var consumer = new RecordingConsumer(1, TimeSpan.Zero);
		var queue = CreateQueue(consumer, limiter);

		_ = queue.Start();
		try
		{
			queue.Publish(new UnthrottledMessage());

			await consumer.WaitUntilComplete(TimeSpan.FromSeconds(5));

			Assert.Equal(0, limiter.AcquireAttempts);
			Assert.Equal(0, queue.MessageCount);
		}
		finally
		{
			await queue.Stop();
		}
	}

	private static QueuedHandlerThreadPool CreateQueue(
		IAsyncHandle<Message> consumer,
		IQueueProcessingLimiter processingLimiter) =>
		new(
			consumer,
			"limited-queue-test",
			new QueueStatsManager(),
			new QueueTrackers(),
			watchSlowMsg: false,
			threadStopWaitTimeout: TimeSpan.FromSeconds(5),
			processingLimiter: processingLimiter);

	private sealed class RecordingConsumer(int expectedCount, TimeSpan delay) : IAsyncHandle<Message>
	{
		private readonly TaskCompletionSource _complete =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		private int _handledCount;
		private int _inFlight;
		private int _maxConcurrency;

		public int HandledCount => Volatile.Read(ref _handledCount);

		public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

		public async ValueTask HandleAsync(Message message, CancellationToken token)
		{
			if (message is CancelledMessage { CancellationToken.IsCancellationRequested: true })
			{
				return;
			}

			var current = Interlocked.Increment(ref _inFlight);
			RecordMaxConcurrency(current);
			try
			{
				await Task.Delay(delay, token);
			}
			finally
			{
				Interlocked.Decrement(ref _inFlight);
			}

			if (Interlocked.Increment(ref _handledCount) == expectedCount)
			{
				_complete.TrySetResult();
			}
		}

		public Task WaitUntilComplete(TimeSpan timeout) =>
			_complete.Task.WaitAsync(timeout);

		private void RecordMaxConcurrency(int current)
		{
			while (true)
			{
				var observed = Volatile.Read(ref _maxConcurrency);
				if (current <= observed)
				{
					return;
				}

				if (Interlocked.CompareExchange(ref _maxConcurrency, current, observed) == observed)
				{
					return;
				}
			}
		}
	}

	private sealed class TestMessage : Message;

	private sealed class CancelledMessage(CancellationToken cancellationToken) : Message(cancellationToken);

	private sealed class UnthrottledMessage : Message;

	private sealed class ControlledProcessingLimiter : IQueueProcessingLimiter
	{
		private readonly SemaphoreSlim _semaphore = new(0);
		private readonly TaskCompletionSource _acquireAttempted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly Func<Message, bool> _shouldLimit;
		private int _acquireAttempts;

		public ControlledProcessingLimiter(Func<Message, bool> shouldLimit = null)
		{
			_shouldLimit = shouldLimit ?? (_ => true);
		}

		public int AcquireAttempts => Volatile.Read(ref _acquireAttempts);

		public bool ShouldLimit(Message message) =>
			_shouldLimit(message);

		public async ValueTask<IDisposable> Acquire(CancellationToken token)
		{
			Interlocked.Increment(ref _acquireAttempts);
			_acquireAttempted.TrySetResult();
			await _semaphore.WaitAsync(token);
			return new Lease(_semaphore);
		}

		public Task WaitForAcquireAttempt(TimeSpan timeout) =>
			_acquireAttempted.Task.WaitAsync(timeout);

		public void ReleaseOne() =>
			_semaphore.Release();

		private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
		{
			public void Dispose() =>
				semaphore.Release();
		}
	}
}
