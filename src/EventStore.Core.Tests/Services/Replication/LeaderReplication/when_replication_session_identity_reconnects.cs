using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Replication;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.TransactionLog.Checkpoint;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Replication.LeaderReplication;

[TestFixture]
public class when_replication_session_identity_reconnects : WithReplicationService
{
	private TestReplicationSession _differentIdentitySession;
	private TestReplicationSession _replacementSession;
	private ICheckpoint _replicationCheckpoint;
	private long _writePosition;

	public override void When()
	{
		_replicationCheckpoint = new InMemoryCheckpoint(-1);
		var replicationTrackingService = new ReplicationTrackingService(
			Publisher,
			5,
			_replicationCheckpoint,
			DbConfig.WriterCheckpoint);
		Publisher.Subscribe<ReplicationTrackingMessage.ReplicaWriteAck>(replicationTrackingService);
		Publisher.Subscribe<SystemMessage.VNodeConnectionLost>(replicationTrackingService);
		Publisher.Subscribe(new AdHocHandler<SystemMessage.VNodeConnectionLost>(message =>
		{
			if (message.SubscriptionId == ReplicaSubscriptionId)
			{
				Service.Handle(new ReplicationMessage.ReplicaLogPositionAck(
					ReplicaSubscriptionId,
					_writePosition,
					_writePosition));
			}
		}));
		replicationTrackingService.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));

		_writePosition = DbConfig.WriterCheckpoint.Read() + 100;
		DbConfig.WriterCheckpoint.Write(_writePosition);
		DbConfig.WriterCheckpoint.Flush();
		replicationTrackingService.Handle(
			new ReplicationTrackingMessage.ReplicaWriteAck(ReplicaSubscriptionId, _writePosition));

		using var certificate = CreateCertificate();
		(_, _differentIdentitySession) = Subscribe(
			ReplicationSessionIdentity.ForClientCertificate(ReplicaId, certificate));
		var replacement = Subscribe(ReplicaSession1.Identity);
		_replacementSession = replacement.Session;
		replicationTrackingService.Handle(
			new ReplicationTrackingMessage.ReplicaWriteAck(replacement.SubscriptionId, _writePosition));
	}

	[Test]
	public void different_authenticated_identity_with_the_same_replica_id_is_not_replaced()
	{
		Assert.That(_differentIdentitySession.IsClosed, Is.False);
	}

	[Test]
	public void same_authenticated_identity_and_replica_id_replaces_the_existing_session()
	{
		AssertEx.IsOrBecomesTrue(() => ReplicaSession1.IsClosed);
		Assert.That(_replacementSession.IsClosed, Is.False);
	}

	[Test]
	public void replaced_session_publishes_connection_lost_once()
	{
		AssertEx.IsOrBecomesTrue(() => ReplicaSession1.IsClosed);
		Assert.That(
			ReplicaLostMessages.Where(message =>
				message.ConnectionId == ReplicaSession1.ConnectionId &&
				message.SubscriptionId == ReplicaSubscriptionId),
			Has.Exactly(1).Items);
	}

	[Test]
	public void retired_subscription_does_not_survive_as_a_quorum_vote()
	{
		Assert.That(_replicationCheckpoint.Read(), Is.LessThan(_writePosition));
	}

	private (Guid SubscriptionId, TestReplicationSession Session) Subscribe(ReplicationSessionIdentity identity)
	{
		var session = new TestReplicationSession(Guid.NewGuid(), identity: identity);
		var subscriptionId = Guid.NewGuid();
		var request = new ReplicationMessage.ReplicaSubscriptionRequest(
			Guid.NewGuid(),
			new NoopEnvelope(),
			session,
			ReplicationSubscriptionVersions.V_CURRENT,
			0,
			Guid.NewGuid(),
			[],
			PortsHelper.GetLoopback(),
			LeaderId,
			subscriptionId,
			true);
		((IAsyncHandle<ReplicationMessage.ReplicaSubscriptionRequest>)Service)
			.HandleAsync(request, default)
			.AsTask()
			.GetAwaiter()
			.GetResult();
		return (subscriptionId, session);
	}

	private static X509Certificate2 CreateCertificate()
	{
		using var key = RSA.Create(2048);
		var request = new CertificateRequest(
			"CN=replica",
			key,
			HashAlgorithmName.SHA256,
			RSASignaturePadding.Pkcs1);
		return request.CreateSelfSigned(
			DateTimeOffset.UtcNow.AddMinutes(-1),
			DateTimeOffset.UtcNow.AddMinutes(1));
	}
}
