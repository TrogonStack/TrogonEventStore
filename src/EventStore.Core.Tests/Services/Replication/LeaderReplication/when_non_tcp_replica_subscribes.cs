using System;
using System.Linq;
using DotNext;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Replication;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Replication.LeaderReplication;

public class when_non_tcp_replica_subscribes : WithReplicationService
{
	private static readonly ReplicationSessionStatistics Statistics = new(
		SendQueueSize: 7,
		TotalBytesSent: 11,
		TotalBytesReceived: 13,
		PendingSendBytes: 17,
		PendingReceivedBytes: 19);

	private TestReplicationSession _session;

	public override void When()
	{
		_session = new TestReplicationSession(Guid.NewGuid(), Statistics);
		var request = new ReplicationMessage.ReplicaSubscriptionRequest(
			Guid.NewGuid(),
			new NoopEnvelope(),
			_session,
			ReplicationSubscriptionVersions.V_CURRENT,
			0,
			Guid.NewGuid(),
			[],
			PortsHelper.GetLoopback(),
			LeaderId,
			Guid.NewGuid(),
			true);

		Service.As<IAsyncHandle<ReplicationMessage.ReplicaSubscriptionRequest>>()
			.HandleAsync(request, default)
			.AsTask()
			.GetAwaiter()
			.GetResult();
	}

	[Test]
	public void sends_leader_messages_through_the_replication_session()
	{
		Assert.That(_session.Messages, Has.Some.InstanceOf<ReplicationMessage.ReplicaSubscribed>());
	}

	[Test]
	public void reports_replication_session_statistics()
	{
		ReplicationMessage.GetReplicationStatsCompleted completed = null;
		Service.Handle(new ReplicationMessage.GetReplicationStats(
			new CallbackEnvelope(message =>
				completed = message as ReplicationMessage.GetReplicationStatsCompleted)));

		var stats = completed?.ReplicationStats.SingleOrDefault(x => x.ConnectionId == _session.ConnectionId);
		var expected = _session.GetStatistics();
		Assert.Multiple(() =>
		{
			Assert.That(stats, Is.Not.Null);
			Assert.That(stats?.SendQueueSize, Is.EqualTo(expected.SendQueueSize));
			Assert.That(stats?.TotalBytesSent, Is.EqualTo(expected.TotalBytesSent));
			Assert.That(stats?.TotalBytesReceived, Is.EqualTo(expected.TotalBytesReceived));
			Assert.That(stats?.PendingSendBytes, Is.EqualTo(expected.PendingSendBytes));
			Assert.That(stats?.PendingReceivedBytes, Is.EqualTo(expected.PendingReceivedBytes));
		});
	}
}
