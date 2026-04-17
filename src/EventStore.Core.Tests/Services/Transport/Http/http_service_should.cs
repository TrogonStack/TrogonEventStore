using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using EventStore.Core.Messages;
using EventStore.Core.Services.Transport.Http;
using EventStore.Core.Tests.Bus.Helpers;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace EventStore.Core.Tests.Services.Transport.Http;

[TestFixture, Category("LongRunning")]
public class http_service_should : SpecificationWithDirectory
{
	[Test]
	[Category("Network")]
	public async Task start_after_system_message_system_init_published()
	{
		await using var node = new MiniNode<LogFormat.V2, string>(PathName);

		node.Node.MainQueue.Publish(new SystemMessage.SystemInit());
		AssertEx.IsOrBecomesTrue(() => node.Node.HttpService.IsListening);
	}

	[Test]
	[Category("Network")]
	public async Task ignore_shutdown_message_that_does_not_say_shut_down()
	{
		await using var node = new MiniNode<LogFormat.V2, string>(PathName);
		node.Node.MainQueue.Publish(new SystemMessage.SystemInit());

		AssertEx.IsOrBecomesTrue(() => node.Node.HttpService.IsListening);

		node.Node.MainQueue.Publish(
			new SystemMessage.BecomeShuttingDown(Guid.NewGuid(), exitProcess: false, shutdownHttp: false));

		Assert.IsTrue(node.Node.HttpService.IsListening);
	}

	[Test]
	[Category("Network")]
	public async Task react_to_shutdown_message_that_cause_process_exit()
	{
		await using var node = new MiniNode<LogFormat.V2, string>(PathName);
		node.Node.MainQueue.Publish(new SystemMessage.SystemInit());

		AssertEx.IsOrBecomesTrue(() => node.Node.HttpService.IsListening);

		node.Node.MainQueue.Publish(
			new SystemMessage.BecomeShuttingDown(Guid.NewGuid(), exitProcess: true, shutdownHttp: true));

		AssertEx.IsOrBecomesTrue(() => !node.Node.HttpService.IsListening);
	}

	[Test]
	[Category("Network")]
	public async Task handle_invalid_characters_in_url()
	{
		await using var node = new MiniNode<LogFormat.V2, string>(PathName);
		node.Node.MainQueue.Publish(new SystemMessage.SystemInit());

		var result = await node.HttpClient.GetAsync("/ping^\"");

		Assert.AreEqual(HttpStatusCode.NotFound, result.StatusCode);
		Assert.IsEmpty(await result.Content.ReadAsStringAsync());
	}
}

[TestFixture]
public class kestrel_http_service_should
{
	[Test]
	public void not_publish_service_shutdown_when_http_shutdown_is_disabled()
	{
		var inputBus = new FakeQueuedHandler();
		var sut = new KestrelHttpService(
			ServiceAccessibility.Public,
			inputBus,
			new TrieUriRouter(),
			multiQueuedHandler: null,
			logHttpRequests: false,
			advertiseAsHost: "127.0.0.1",
			advertiseAsPort: 2113,
			disableAuthorization: false,
			new IPEndPoint(IPAddress.Loopback, 2113));

		sut.Handle(new SystemMessage.SystemInit());
		inputBus.PublishedMessages.Clear();

		sut.Handle(new SystemMessage.BecomeShuttingDown(Guid.NewGuid(), exitProcess: false, shutdownHttp: false));

		Assert.That(inputBus.PublishedMessages.OfType<SystemMessage.ServiceShutdown>(), Is.Empty);
	}

	[Test]
	public void publish_service_shutdown_when_http_shutdown_is_enabled()
	{
		var inputBus = new FakeQueuedHandler();
		var sut = new KestrelHttpService(
			ServiceAccessibility.Public,
			inputBus,
			new TrieUriRouter(),
			multiQueuedHandler: null,
			logHttpRequests: false,
			advertiseAsHost: "127.0.0.1",
			advertiseAsPort: 2113,
			disableAuthorization: false,
			new IPEndPoint(IPAddress.Loopback, 2113));

		sut.Handle(new SystemMessage.SystemInit());
		inputBus.PublishedMessages.Clear();

		sut.Handle(new SystemMessage.BecomeShuttingDown(Guid.NewGuid(), exitProcess: false, shutdownHttp: true));

		Assert.That(inputBus.PublishedMessages.OfType<SystemMessage.ServiceShutdown>(), Has.Exactly(1).Items);
	}
}

[TestFixture, Category("LongRunning")]
public class when_http_request_times_out : SpecificationWithDirectory
{
	[Test]
	[Category("Network")]
	public async Task should_throw_an_exception()
	{
		var timeoutSec = 2;
		var sleepFor = timeoutSec + 1;

		await using var node = new MiniNode<LogFormat.V2, string>(PathName, httpClientTimeoutSec: timeoutSec);
		await node.Start();

		Assert.ThrowsAsync<TaskCanceledException>(() => node.HttpClient
				.GetAsync(string.Format("/test-timeout?sleepfor={0}", sleepFor * 1000)),
			message: "The client aborted the request.");
	}
}
