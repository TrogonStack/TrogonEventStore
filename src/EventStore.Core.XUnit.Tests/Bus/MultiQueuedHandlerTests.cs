using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Monitoring.Stats;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Bus;

public class MultiQueuedHandlerTests
{
	[Fact]
	public void publishes_messages_with_the_same_synchronization_group_to_the_same_queue()
	{
		var queues = CreateQueues(4);
		var sut = new MultiQueuedHandler(queues.Length, i => queues[i]);
		var group = new object();
		var first = new TestMessage(group);
		var second = new TestMessage(group);

		sut.Publish(first);
		sut.Publish(second);

		var firstQueue = queues.Single(q => q.Messages.Contains(first));
		var secondQueue = queues.Single(q => q.Messages.Contains(second));
		Assert.Same(firstQueue, secondQueue);
	}

	[Fact]
	public void publishes_messages_without_a_synchronization_group_round_robin()
	{
		var queues = CreateQueues(2);
		var sut = new MultiQueuedHandler(queues.Length, i => queues[i]);
		var first = new TestMessage();
		var second = new TestMessage();
		var third = new TestMessage();

		sut.Publish(first);
		sut.Publish(second);
		sut.Publish(third);

		Assert.Equal([first, third], queues[0].Messages);
		Assert.Equal([second], queues[1].Messages);
	}

	[Fact]
	public async Task stop_waits_for_all_queues()
	{
		var queues = CreateQueues(2);
		var firstStop = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondStop = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
		queues[0].StopTask = firstStop.Task;
		queues[1].StopTask = secondStop.Task;
		var sut = new MultiQueuedHandler(queues.Length, i => queues[i]);

		var stop = sut.Stop();
		Assert.False(stop.IsCompleted);

		firstStop.SetResult(null);
		Assert.False(stop.IsCompleted);

		secondStop.SetResult(null);
		await stop;
	}

	[Fact]
	public async Task stop_propagates_queue_stop_failures()
	{
		var queues = CreateQueues(2);
		var failure = new InvalidOperationException("stop failed");
		queues[0].StopTask = Task.FromException(failure);
		var sut = new MultiQueuedHandler(queues.Length, i => queues[i]);

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(sut.Stop);

		Assert.Same(failure, ex);
	}

	[Fact]
	public async Task stop_attempts_all_queues_when_one_stop_throws_synchronously()
	{
		var queues = CreateQueues(2);
		var failure = new InvalidOperationException("stop failed");
		queues[0].StopException = failure;
		var sut = new MultiQueuedHandler(queues.Length, i => queues[i]);

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(sut.Stop);

		Assert.Same(failure, ex);
		Assert.Equal(1, queues[1].StopCount);
	}

	private static RecordingQueuedHandler[] CreateQueues(int count)
	{
		var queues = new RecordingQueuedHandler[count];
		for (var i = 0; i < queues.Length; i++)
		{
			queues[i] = new RecordingQueuedHandler($"queue-{i}");
		}

		return queues;
	}

	private sealed class TestMessage(object synchronizationGroup = null) : Message
	{
		public override object SynchronizationGroup => synchronizationGroup;
	}

	private sealed class RecordingQueuedHandler(string name) : IQueuedHandler
	{
		public string Name { get; } = name;

		public List<Message> Messages { get; } = [];

		public Task StopTask { get; set; } = Task.CompletedTask;

		public Exception StopException { get; set; }

		public int StopCount { get; private set; }

		public void Publish(Message message) => Messages.Add(message);

		public Task Start() => Task.CompletedTask;

		public Task Stop()
		{
			StopCount++;
			if (StopException is not null)
			{
				throw StopException;
			}

			return StopTask;
		}

		public void RequestStop()
		{
		}

		public QueueStats GetStatistics() =>
			new(Name, string.Empty, Messages.Count, 0, 0, 0, null, null, Messages.Count, 0, 0, null, null);
	}
}
