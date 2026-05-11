using System;
using System.Net;
using DotNext.Net.Http;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Services;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Tests.Bus.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.VNode;

public class ShutdownServiceTests
{
	private VNodeInfo BogusNodeInfo { get; }
		= new(
			Guid.NewGuid(),
			0,
			new IPEndPoint(0, 0),
			new IPEndPoint(IPAddress.Loopback, 1),
			new IPEndPoint(IPAddress.Loopback, 2),
			new IPEndPoint(IPAddress.Loopback, 3),
			new HttpEndPoint(new Uri("http://www.trogondb.com")), true);

	[Test]
	public void GracefulShutdown_ShouldTriggerTerminationCallback_AndScheduleMessages()
	{
		var queue = new FakeQueuedHandler();
		var sut = new ShutdownService(queue, BogusNodeInfo);
		var terminated = false;

		sut.Handle(new SystemMessage.RegisterForGracefulTermination("foo", () => terminated = true));
		sut.Handle(new ClientMessage.RequestShutdown(true, true));
		sut.Handle(new SystemMessage.ComponentTerminated("foo"));

		Assert.True(terminated);
		Assert.IsInstanceOf<TimerMessage.Schedule>(queue.PublishedMessages[0]);
		Assert.IsInstanceOf<SystemMessage.BecomeShuttingDown>(queue.PublishedMessages[1]);
	}

	[Test]
	public void Shutdown_ShouldProceed_WhenComponentAlreadyReportedTermination()
	{
		var queue = new FakeQueuedHandler();
		var sut = new ShutdownService(queue, BogusNodeInfo);
		var notCalled = true;

		sut.Handle(new SystemMessage.RegisterForGracefulTermination("foo", () => notCalled = false));
		sut.Handle(new SystemMessage.ComponentTerminated("foo"));
		sut.Handle(new ClientMessage.RequestShutdown(true, true));

		// Proof that callback in the RegisterForGraceFulTermination message was never called.
		Assert.True(notCalled);
		Assert.IsInstanceOf<SystemMessage.BecomeShuttingDown>(queue.PublishedMessages[0]);
	}

	[Test]
	public void Shutdown_ShouldProceed_WhenComponentDoesNotReportTermination_AfterTimeout()
	{
		var queue = new FakeQueuedHandler();
		var sut = new ShutdownService(queue, BogusNodeInfo);
		var called = false;

		sut.Handle(new SystemMessage.RegisterForGracefulTermination("foo", () => called = true));
		sut.Handle(new ClientMessage.RequestShutdown(true, true));
		sut.Handle(new SystemMessage.PeripheralShutdownTimeout());

		Assert.True(called);
		Assert.IsInstanceOf<TimerMessage.Schedule>(queue.PublishedMessages[0]);
		Assert.IsInstanceOf<SystemMessage.BecomeShuttingDown>(queue.PublishedMessages[1]);
	}
}
