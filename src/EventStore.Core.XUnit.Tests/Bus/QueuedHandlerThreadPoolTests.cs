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
}
