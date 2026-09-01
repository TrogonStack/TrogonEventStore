using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Replication;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Services.Transport.Tcp;
using EventStore.Core.Services.UserManagement;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Tcp;

[TestFixture]
public class TcpRequestForwardingServiceTests
{
	[TestCase(TcpForwardingTransport.Plaintext)]
	[TestCase(TcpForwardingTransport.Tls)]
	public void pre_replica_selects_the_configured_leader_endpoint(TcpForwardingTransport transport)
	{
		var fixture = CreateFixture(transport);

		fixture.Service.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));

		var expected = transport == TcpForwardingTransport.Tls
			? fixture.Leader.InternalSecureTcpEndPoint
			: fixture.Leader.InternalTcpEndPoint;
		Assert.That(fixture.Factory.Targets.Single().EndPoint, Is.EqualTo(expected));
	}

	[Test]
	public void all_supported_writes_are_forwarded_without_rewriting_the_request()
	{
		var fixture = CreateFixture();
		fixture.Service.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var connection = fixture.Factory.Connections.Single();
		var requests = CreateWriteRequests();

		foreach (var request in requests)
		{
			fixture.Service.Handle(new ClientMessage.TcpForwardMessage(request));
		}

		Assert.That(connection.Sent, Is.EqualTo(requests).Using<Message>(ReferenceEquals));
	}

	[Test]
	public void replication_messages_cannot_use_the_forwarding_connection()
	{
		var fixture = CreateFixture();
		fixture.Service.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));

		foreach (var frame in CreateReplicationFrames())
		{
			var message = new ClientMessage.TcpForwardMessage(frame);
			Assert.That(() => fixture.Service.Handle(message), Throws.ArgumentException);
		}

		Assert.That(fixture.Factory.Connections.Single().Sent, Is.Empty);
	}

	[Test]
	public void connection_callbacks_do_not_publish_replication_connection_events()
	{
		var fixture = CreateFixture();
		fixture.Service.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var connection = fixture.Factory.Connections.Single();

		connection.Establish();
		connection.Close(SocketError.ConnectionReset);
		connection.Close(SocketError.ConnectionReset);

		Assert.Multiple(() =>
		{
			Assert.That(fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionEstablished>(), Is.Empty);
			Assert.That(fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionLost>(), Is.Empty);
			Assert.That(fixture.Publisher.Messages.OfType<TimerMessage.Schedule>(), Has.Exactly(1).Items);
		});
	}

	[Test]
	public void a_closed_connection_reconnects_independently_while_follower()
	{
		var fixture = CreateFixture();
		var stateCorrelationId = Guid.NewGuid();
		fixture.Service.Handle(new SystemMessage.BecomePreReplica(
			stateCorrelationId, Guid.NewGuid(), fixture.Leader));
		fixture.Service.Handle(new SystemMessage.BecomeFollower(stateCorrelationId, fixture.Leader));
		fixture.Factory.Connections.Single().Close(SocketError.ConnectionReset);
		var schedule = fixture.Publisher.Messages.OfType<TimerMessage.Schedule>().Single();

		fixture.Service.Handle((TcpRequestForwardingMessage.Reconnect)schedule.ReplyMessage);

		Assert.That(fixture.Factory.Connections, Has.Count.EqualTo(2));
	}

	[Test]
	public void a_failed_connection_attempt_retries_without_affecting_replication_lifecycle()
	{
		var fixture = CreateFixture();
		fixture.Factory.CreateException = new SocketException((int)SocketError.ConnectionRefused);

		fixture.Service.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var schedule = fixture.Publisher.Messages.OfType<TimerMessage.Schedule>().Single();

		fixture.Service.Handle((TcpRequestForwardingMessage.Reconnect)schedule.ReplyMessage);

		Assert.Multiple(() =>
		{
			Assert.That(fixture.Factory.Connections, Has.Exactly(1).Items);
			Assert.That(fixture.Publisher.Messages.OfType<ReplicationMessage.LeaderConnectionFailed>(), Is.Empty);
			Assert.That(fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionLost>(), Is.Empty);
		});
	}

	[Test]
	public void a_stale_connection_close_does_not_schedule_a_reconnect()
	{
		var fixture = CreateFixture();
		fixture.Service.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var staleConnection = fixture.Factory.Connections.Single();

		fixture.Service.Handle(new ReplicationMessage.ReconnectToLeader(Guid.NewGuid(), CreateLeader()));
		staleConnection.Close(SocketError.ConnectionReset);

		Assert.That(fixture.Publisher.Messages.OfType<TimerMessage.Schedule>(), Is.Empty);
	}

	[Test]
	public void replication_reconnect_keeps_a_healthy_connection_to_the_same_leader()
	{
		var fixture = CreateFixture();
		fixture.Service.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var connection = fixture.Factory.Connections.Single();

		fixture.Service.Handle(new ReplicationMessage.ReconnectToLeader(Guid.NewGuid(), fixture.Leader));

		Assert.Multiple(() =>
		{
			Assert.That(fixture.Factory.Connections, Has.Exactly(1).Items);
			Assert.That(connection.StopCalls, Is.Zero);
		});
	}

	[Test]
	public void replication_reconnect_replaces_the_connection_when_the_leader_target_changes()
	{
		var fixture = CreateFixture();
		fixture.Service.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var connection = fixture.Factory.Connections.Single();
		var movedLeader = CreateLeader(fixture.Leader.InstanceId, plaintextPort: 1212, tlsPort: 1213);

		fixture.Service.Handle(new ReplicationMessage.ReconnectToLeader(Guid.NewGuid(), movedLeader));

		Assert.Multiple(() =>
		{
			Assert.That(fixture.Factory.Connections, Has.Exactly(2).Items);
			Assert.That(connection.StopCalls, Is.EqualTo(1));
		});
	}

	[TestCase(TcpForwardingTransport.Plaintext)]
	[TestCase(TcpForwardingTransport.Tls)]
	public void replication_reconnect_schedules_retry_when_the_leader_lacks_the_configured_endpoint(
		TcpForwardingTransport transport)
	{
		var fixture = CreateFixture(transport);
		fixture.Service.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var leaderWithoutTarget = CreateLeader(
			fixture.Leader.InstanceId,
			includePlaintext: transport != TcpForwardingTransport.Plaintext,
			includeTls: transport != TcpForwardingTransport.Tls);

		Assert.That(
			() => fixture.Service.Handle(new ReplicationMessage.ReconnectToLeader(Guid.NewGuid(), leaderWithoutTarget)),
			Throws.Nothing);
		Assert.That(fixture.Publisher.Messages.OfType<TimerMessage.Schedule>(), Has.Exactly(1).Items);
	}

	[Test]
	public void leaving_replica_states_closes_the_forwarding_connection()
	{
		var fixture = CreateFixture();
		fixture.Service.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var connection = fixture.Factory.Connections.Single();

		fixture.Service.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));

		Assert.That(connection.StopCalls, Is.EqualTo(1));
	}

	[Test]
	public void an_in_flight_forward_request_is_ignored_after_leaving_replica_states()
	{
		var fixture = CreateFixture();
		fixture.Service.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var connection = fixture.Factory.Connections.Single();
		var message = new ClientMessage.TcpForwardMessage(CreateWriteRequests().First());

		fixture.Service.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));

		Assert.Multiple(() =>
		{
			Assert.That(() => fixture.Service.Handle(message), Throws.Nothing);
			Assert.That(connection.Sent, Is.Empty);
		});
	}

	private static Fixture CreateFixture(
		TcpForwardingTransport transport = TcpForwardingTransport.Plaintext)
	{
		var publisher = new CapturingPublisher();
		var factory = new FakeConnectionFactory();
		return new Fixture(
			new TcpRequestForwardingService(publisher, factory, transport, TimeSpan.FromMilliseconds(100)),
			factory,
			publisher,
			CreateLeader());
	}

	private static IReadOnlyList<Message> CreateWriteRequests()
	{
		var user = new ClaimsPrincipal();
		var @event = new Event(Guid.NewGuid(), "type", true, Array.Empty<byte>(), Array.Empty<byte>());
		return new Message[]
		{
			new ClientMessage.WriteEvents(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, true,
				"stream", ExpectedVersion.Any, @event, user),
			new ClientMessage.TransactionStart(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, true,
				"stream", ExpectedVersion.Any, user),
			new ClientMessage.TransactionWrite(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, true,
				1, new[] { @event }, user),
			new ClientMessage.TransactionCommit(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, true, 1, user),
			new ClientMessage.DeleteStream(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, true,
				"stream", ExpectedVersion.Any, false, user)
		};
	}

	private static IReadOnlyList<Message> CreateReplicationFrames()
	{
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		return new Message[]
		{
			new ReplicationMessage.SubscribeReplica(
				0,
				0,
				Guid.Empty,
				Array.Empty<EpochRecord>(),
				new DnsEndPoint("replica.internal", 1112),
				leaderId,
				subscriptionId,
				true,
				Guid.NewGuid()),
			new ReplicationMessage.AckLogPosition(subscriptionId, 100, 90)
		};
	}

	private static MemberInfo CreateLeader(
		Guid? instanceId = null,
		int plaintextPort = 1112,
		int tlsPort = 1113,
		bool includePlaintext = true,
		bool includeTls = true) => MemberInfo.ForVNode(
		instanceId ?? Guid.NewGuid(),
		DateTime.UtcNow,
		VNodeState.Leader,
		true,
		includePlaintext ? new DnsEndPoint("leader-replication.internal", plaintextPort) : null,
		includeTls ? new DnsEndPoint("leader-replication.internal", tlsPort) : null,
		null,
		null,
		new DnsEndPoint("leader.internal", 2113),
		null,
		0,
		0,
		0,
		0,
		0,
		0,
		0,
		Guid.NewGuid(),
		0,
		false);

	private sealed record Fixture(
		TcpRequestForwardingService Service,
		FakeConnectionFactory Factory,
		CapturingPublisher Publisher,
		MemberInfo Leader);

	private sealed class FakeConnectionFactory : ITcpForwardingConnectionFactory
	{
		public List<TcpForwardingConnectionTarget> Targets { get; } = new();
		public List<FakeConnection> Connections { get; } = new();
		public Exception CreateException { get; set; }

		public ITcpForwardingConnection Create(
			TcpForwardingConnectionTarget target,
			Action onEstablished,
			Action<SocketError> onClosed)
		{
			Targets.Add(target);
			if (CreateException is not null)
			{
				var exception = CreateException;
				CreateException = null;
				throw exception;
			}

			var connection = new FakeConnection(target.EndPoint, onEstablished, onClosed);
			Connections.Add(connection);
			return connection;
		}
	}

	private sealed class FakeConnection(
		EndPoint remoteEndPoint,
		Action onEstablished,
		Action<SocketError> onClosed) : ITcpForwardingConnection
	{
		public EndPoint RemoteEndPoint { get; } = remoteEndPoint;
		public List<Message> Sent { get; } = new();
		public int StopCalls { get; private set; }

		public void StartReceiving()
		{
		}

		public void Send(Message message) => Sent.Add(message);

		public void Stop(string reason) => StopCalls++;

		public void Establish() => onEstablished();

		public void Close(SocketError socketError) => onClosed(socketError);
	}

	private sealed class CapturingPublisher : IPublisher
	{
		public List<Message> Messages { get; } = new();

		public void Publish(Message message) => Messages.Add(message);
	}
}

[TestFixture]
public class TcpForwardingDispatcherTests
{
	private readonly TcpForwardingDispatcher _dispatcher = new(TimeSpan.FromSeconds(5));

	[Test]
	public void write_packages_preserve_internal_correlation_and_trusted_identity()
	{
		var internalCorrelationId = Guid.NewGuid();
		var message = new ClientMessage.WriteEvents(
			internalCorrelationId,
			Guid.NewGuid(),
			IEnvelope.NoOp,
			true,
			"stream",
			ExpectedVersion.Any,
			new Event(Guid.NewGuid(), "type", true, Array.Empty<byte>(), Array.Empty<byte>()),
			SystemAccounts.System);

		var package = _dispatcher.WrapMessage(message, (byte)ClientVersion.V2);

		Assert.Multiple(() =>
		{
			Assert.That(package, Is.Not.Null);
			Assert.That(package.Value.CorrelationId, Is.EqualTo(internalCorrelationId));
			Assert.That(package.Value.Flags, Is.EqualTo(TcpFlags.TrustedWrite));
		});
	}

	[Test]
	public void write_packages_preserve_explicit_credentials()
	{
		var message = new ClientMessage.TransactionStart(
			Guid.NewGuid(),
			Guid.NewGuid(),
			IEnvelope.NoOp,
			true,
			"stream",
			ExpectedVersion.Any,
			new ClaimsPrincipal(),
			new Dictionary<string, string> { ["uid"] = "admin", ["pwd"] = "changeit" });

		var package = _dispatcher.WrapMessage(message, (byte)ClientVersion.V2);

		Assert.Multiple(() =>
		{
			Assert.That(package.Value.Flags, Is.EqualTo(TcpFlags.Authenticated));
			Assert.That(package.Value.Tokens["uid"], Is.EqualTo("admin"));
			Assert.That(package.Value.Tokens["pwd"], Is.EqualTo("changeit"));
		});
	}

	[Test]
	public void completion_packages_preserve_the_forwarding_correlation()
	{
		var internalCorrelationId = Guid.NewGuid();
		var package = _dispatcher.WrapMessage(
			new ClientMessage.WriteEventsCompleted(internalCorrelationId, 0, 0, 10, 10),
			(byte)ClientVersion.V2);

		var completion = (ClientMessage.WriteEventsCompleted)_dispatcher.UnwrapPackage(
			package.Value,
			IEnvelope.NoOp,
			new ClaimsPrincipal(),
			new Dictionary<string, string>(),
			null,
			(byte)ClientVersion.V2);

		Assert.That(completion.CorrelationId, Is.EqualTo(internalCorrelationId));
	}

	[Test]
	public void replication_frames_are_not_registered()
	{
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		Message[] frames =
		{
			new ReplicationMessage.SubscribeReplica(
				0,
				0,
				Guid.Empty,
				Array.Empty<EpochRecord>(),
				new DnsEndPoint("replica.internal", 1112),
				leaderId,
				subscriptionId,
				true,
				Guid.NewGuid()),
			new ReplicationMessage.AckLogPosition(subscriptionId, 100, 90)
		};

		Assert.That(
			frames.Select(frame => _dispatcher.WrapMessage(frame, (byte)ClientVersion.V2)),
			Is.All.Null);
	}
}
