using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using EventStore.Core.Authentication.InternalAuthentication;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Core.Services.Transport.Tcp;
using EventStore.Core.Services.UserManagement;
using EventStore.Core.Services.VNode;
using EventStore.Core.Tests.Authentication;
using EventStore.Core.Tests.Authorization;
using EventStore.Core.Tests.Fakes;
using EventStore.Core.Util;
using Google.Protobuf;
using NUnit.Framework;
using ClientNotHandled = EventStore.Client.Messages.NotHandled;

namespace EventStore.Core.Tests.Services.Transport.Tcp;

[TestFixture]
public class TcpForwardingErrorResponseTests
{
	[Test]
	public void forwarding_connection_sends_error_responses()
	{
		var notHandled = SendAndRead(new ClientMessage.NotHandled(
			Guid.NewGuid(),
			ClientMessage.NotHandled.Types.NotHandledReason.NotReady,
			"not ready"));
		var notAuthenticated = SendAndRead(new TcpMessage.NotAuthenticated(Guid.NewGuid(), "not authenticated"));

		Assert.Multiple(() =>
		{
			Assert.That(notHandled?.Command, Is.EqualTo(TcpCommand.NotHandled));
			Assert.That(notAuthenticated?.Command, Is.EqualTo(TcpCommand.NotAuthenticated));
		});
	}

	[Test]
	public void forwarding_dispatcher_receives_error_responses()
	{
		var clientDispatcher = new ClientTcpDispatcher(TimeSpan.FromSeconds(5));
		var forwardingDispatcher = new TcpForwardingDispatcher(TimeSpan.FromSeconds(5));
		var notHandled = new ClientMessage.NotHandled(
			Guid.NewGuid(),
			ClientMessage.NotHandled.Types.NotHandledReason.NotReady,
			"not ready");
		var notAuthenticated = new TcpMessage.NotAuthenticated(Guid.NewGuid(), "not authenticated");

		var receivedNotHandled = Unwrap(forwardingDispatcher,
			clientDispatcher.WrapMessage(notHandled, (byte)ClientVersion.V2));
		var receivedNotAuthenticated = Unwrap(forwardingDispatcher,
			clientDispatcher.WrapMessage(notAuthenticated, (byte)ClientVersion.V2));

		var forwardedNotHandled = receivedNotHandled as ClientMessage.NotHandled;
		var forwardedNotAuthenticated = receivedNotAuthenticated as TcpMessage.NotAuthenticated;
		Assert.Multiple(() =>
		{
			Assert.That(forwardedNotHandled?.CorrelationId, Is.EqualTo(notHandled.CorrelationId));
			Assert.That(forwardedNotHandled?.Reason, Is.EqualTo(notHandled.Reason));
			Assert.That(forwardedNotAuthenticated?.CorrelationId, Is.EqualTo(notAuthenticated.CorrelationId));
			Assert.That(forwardedNotAuthenticated?.Reason, Is.EqualTo(notAuthenticated.Reason));
		});
	}

	[Test]
	public void forwarding_dispatcher_maps_an_unknown_not_handled_reason_to_not_ready()
	{
		var dispatcher = new TcpForwardingDispatcher(TimeSpan.FromSeconds(5));
		var dto = new ClientNotHandled
		{
			Reason = (ClientNotHandled.Types.NotHandledReason)int.MaxValue
		};
		var package = new TcpPackage(TcpCommand.NotHandled, Guid.NewGuid(), dto.ToByteArray());

		ClientMessage.NotHandled received = null;
		Assert.That(() => received = Unwrap(dispatcher, package) as ClientMessage.NotHandled, Throws.Nothing);
		Assert.That(received?.Reason, Is.EqualTo(ClientMessage.NotHandled.Types.NotHandledReason.NotReady));
	}

	[Test]
	public void forwarding_dispatcher_uses_the_non_empty_secure_leader_endpoint()
	{
		var dispatcher = new TcpForwardingDispatcher(TimeSpan.FromSeconds(5));
		var leaderInfo = new ClientNotHandled.Types.LeaderInfo(
			string.Empty,
			0,
			"leader.internal",
			2113,
			"leader-secure.internal",
			1113);
		var dto = new ClientNotHandled
		{
			Reason = ClientNotHandled.Types.NotHandledReason.NotLeader,
			AdditionalInfo = leaderInfo.ToByteString()
		};
		var package = new TcpPackage(TcpCommand.NotHandled, Guid.NewGuid(), dto.ToByteArray());

		var received = (ClientMessage.NotHandled)Unwrap(dispatcher, package);

		Assert.Multiple(() =>
		{
			Assert.That(received.LeaderInfo.IsSecure, Is.True);
			Assert.That(received.LeaderInfo.ExternalTcp,
				Is.EqualTo(new DnsEndPoint("leader-secure.internal", 1113)));
		});
	}

	[Test]
	public void not_authenticated_is_forwarded_to_the_original_client()
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
		var service = new RequestForwardingService(new NoopPublisher(), forwardingProxy, TimeSpan.FromSeconds(1));

		Assert.That(service, Is.InstanceOf<IHandle<TcpMessage.NotAuthenticated>>());
		if (service is not IHandle<TcpMessage.NotAuthenticated> handler)
		{
			return;
		}

		handler.Handle(new TcpMessage.NotAuthenticated(internalCorrelationId, "not authenticated"));

		var forwarded = response as TcpMessage.NotAuthenticated;
		Assert.Multiple(() =>
		{
			Assert.That(forwarded, Is.Not.Null);
			Assert.That(forwarded?.CorrelationId, Is.EqualTo(clientCorrelationId));
			Assert.That(forwarded?.Reason, Is.EqualTo("not authenticated"));
		});
	}

	[Test]
	public void forwarding_connection_publishes_received_error_responses()
	{
		var publisher = new CapturingPublisher();
		var manager = CreateManager(new DummyTcpConnection(), publisher);
		var clientDispatcher = new ClientTcpDispatcher(TimeSpan.FromSeconds(5));
		Message[] responses =
		{
			new ClientMessage.NotHandled(
				Guid.NewGuid(),
				ClientMessage.NotHandled.Types.NotHandledReason.NotReady,
				"not ready"),
			new TcpMessage.NotAuthenticated(Guid.NewGuid(), "not authenticated")
		};

		foreach (var response in responses)
		{
			manager.ProcessPackage(clientDispatcher.WrapMessage(response, (byte)ClientVersion.V2).Value);
		}

		Assert.Multiple(() =>
		{
			Assert.That(publisher.Messages, Has.Exactly(1).TypeOf<ClientMessage.NotHandled>());
			Assert.That(publisher.Messages, Has.Exactly(1).TypeOf<TcpMessage.NotAuthenticated>());
		});
	}

	private static TcpPackage? SendAndRead(Message message)
	{
		var connection = new DummyTcpConnection();
		var manager = CreateManager(connection, new NoopPublisher());

		manager.SendMessage(message);

		return connection.ReceivedData is null
			? null
			: TcpPackage.FromArraySegment(connection.ReceivedData.Last());
	}

	private static TcpConnectionManager CreateManager(DummyTcpConnection connection, IPublisher publisher) =>
		new(
			Guid.NewGuid().ToString(),
			TcpServiceType.Internal,
			new TcpForwardingDispatcher(TimeSpan.FromSeconds(5)),
			publisher,
			connection,
			new SynchronousScheduler(),
			new InternalAuthenticationProvider(
				InMemoryBus.CreateTest(),
				new Core.Helpers.IODispatcher(new SynchronousScheduler(), IEnvelope.NoOp),
				new StubPasswordHashAlgorithm(),
				1,
				false,
				DefaultData.DefaultUserOptions),
			new AuthorizationGateway(new TestAuthorizationProvider()),
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(10),
			(_, _) => { },
			Opts.ConnectionPendingSendBytesThresholdDefault,
			Opts.ConnectionQueueSizeThresholdDefault);

	private static Message Unwrap(TcpForwardingDispatcher dispatcher, TcpPackage? package) =>
		package is null
			? null
			: dispatcher.UnwrapPackage(
				package.Value,
				IEnvelope.NoOp,
				new ClaimsPrincipal(),
				new Dictionary<string, string>(),
				null,
				(byte)ClientVersion.V2);

	private sealed class CapturingPublisher : IPublisher
	{
		public List<Message> Messages { get; } = new();

		public void Publish(Message message) => Messages.Add(message);
	}
}
