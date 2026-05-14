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

		public void Publish(Message message) => Messages.Add(message);

		public Task Start() => Task.CompletedTask;

		public Task Stop() => Task.CompletedTask;

		public void RequestStop()
		{
		}

		public QueueStats GetStatistics() =>
			new(Name, string.Empty, Messages.Count, 0, 0, 0, null, null, Messages.Count, 0, 0, null, null);
	}
}
