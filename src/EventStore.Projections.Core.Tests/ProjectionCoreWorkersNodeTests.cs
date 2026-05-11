using System;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Monitoring.Stats;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests;

[TestFixture]
public class ProjectionCoreWorkersNodeTests
{
	[Test]
	public void stop_should_attempt_to_stop_output_queue_even_if_input_stop_fails()
	{
		var inputException = new InvalidOperationException("input");
		var inputQueue = new FakeQueuedHandler(() => Task.FromException(inputException));
		var outputQueue = new FakeQueuedHandler(() => Task.CompletedTask);
		var sut = new CoreWorker(Guid.NewGuid(), inputQueue, outputQueue);

		var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await sut.Stop());

		Assert.That(ex, Is.SameAs(inputException));
		Assert.That(inputQueue.StopCalls, Is.EqualTo(1));
		Assert.That(outputQueue.StopCalls, Is.EqualTo(1));
	}

	[Test]
	public void stop_should_aggregate_failures_from_both_queues()
	{
		var inputException = new InvalidOperationException("input");
		var outputException = new InvalidOperationException("output");
		var inputQueue = new FakeQueuedHandler(() => Task.FromException(inputException));
		var outputQueue = new FakeQueuedHandler(() => Task.FromException(outputException));
		var sut = new CoreWorker(Guid.NewGuid(), inputQueue, outputQueue);

		var ex = Assert.ThrowsAsync<AggregateException>(async () => await sut.Stop());

		Assert.That(ex!.InnerExceptions, Has.Count.EqualTo(2));
		Assert.That(ex.InnerExceptions, Has.Some.SameAs(inputException));
		Assert.That(ex.InnerExceptions, Has.Some.SameAs(outputException));
		Assert.That(inputQueue.StopCalls, Is.EqualTo(1));
		Assert.That(outputQueue.StopCalls, Is.EqualTo(1));
	}

	private sealed class FakeQueuedHandler : IQueuedHandler
	{
		private readonly Func<Task> _stop;

		public FakeQueuedHandler(Func<Task> stop) => _stop = stop;

		public int StopCalls { get; private set; }

		public string Name => nameof(FakeQueuedHandler);
		public Task Start() => Task.CompletedTask;
		public void RequestStop() { }
		public QueueStats GetStatistics() => null;
		public void Publish(Message message) { }

		public Task Stop()
		{
			StopCalls++;
			return _stop();
		}
	}
}
