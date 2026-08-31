using System;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Core.Services.VNode;
using EventStore.Core.Tests.Fakes;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.RequestForwarding;

[TestFixture]
public class RequestForwardingServiceTests
{
	[Test]
	public void transaction_commit_positions_survive_the_client_correlation_rewrite()
	{
		var internalCorrelationId = Guid.NewGuid();
		var clientCorrelationId = Guid.NewGuid();
		Message response = null;
		var forwardingProxy = new MessageForwardingProxy();
		forwardingProxy.Register(
			internalCorrelationId,
			clientCorrelationId,
			new CallbackEnvelope(message => response = message),
			TimeSpan.FromMinutes(1),
			new ClientMessage.TransactionCommitCompleted(
				clientCorrelationId, 42, OperationResult.ForwardTimeout, "timeout"));
		var service = new RequestForwardingService(
			new NoopPublisher(), forwardingProxy, TimeSpan.FromSeconds(1));

		service.Handle(new ClientMessage.TransactionCommitCompleted(
			internalCorrelationId, 42, 10, 12, 1_000, 1_100));

		var completion = (ClientMessage.TransactionCommitCompleted)response;
		Assert.Multiple(() =>
		{
			Assert.That(completion.CorrelationId, Is.EqualTo(clientCorrelationId));
			Assert.That(completion.TransactionId, Is.EqualTo(42));
			Assert.That(completion.FirstEventNumber, Is.EqualTo(10));
			Assert.That(completion.LastEventNumber, Is.EqualTo(12));
			Assert.That(completion.PreparePosition, Is.EqualTo(1_000));
			Assert.That(completion.CommitPosition, Is.EqualTo(1_100));
		});
	}

	[Test]
	public void not_authenticated_survives_the_client_correlation_rewrite()
	{
		var internalCorrelationId = Guid.NewGuid();
		var clientCorrelationId = Guid.NewGuid();
		Message response = null;
		var forwardingProxy = new MessageForwardingProxy();
		forwardingProxy.Register(
			internalCorrelationId,
			clientCorrelationId,
			new CallbackEnvelope(message => response = message),
			TimeSpan.FromMinutes(1),
			new TcpMessage.NotAuthenticated(clientCorrelationId, "timeout"));
		var service = new RequestForwardingService(
			new NoopPublisher(), forwardingProxy, TimeSpan.FromSeconds(1));

		service.Handle(new TcpMessage.NotAuthenticated(internalCorrelationId, "not authenticated"));

		var completion = (TcpMessage.NotAuthenticated)response;
		Assert.Multiple(() =>
		{
			Assert.That(completion.CorrelationId, Is.EqualTo(clientCorrelationId));
			Assert.That(completion.Reason, Is.EqualTo("not authenticated"));
		});
	}
}
