using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNext;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Replication;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Replication.LeaderReplication;

public class WhenEofPositionUpdateIsRejected<TLogFormat, TStreamId>
	: WithReplicationServiceAndEpochManager<TLogFormat, TStreamId>
{
	private RejectingPositionUpdateSession _session;

	public override async Task When(CancellationToken token = default)
	{
		long eventNumber = 0;
		while (Writer.Position <= Db.Config.ChunkSize)
		{
			await Writer.Write(CreateLogRecord(eventNumber++), token);
		}

		await Writer.Flush(token);

		var subscriptionId = Guid.NewGuid();
		_session = new RejectingPositionUpdateSession(Guid.NewGuid());
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
			subscriptionId,
			true);

		await Service.As<IAsyncHandle<ReplicationMessage.ReplicaSubscriptionRequest>>()
			.HandleAsync(request, token);
		AssertEx.IsOrBecomesTrue(
			() => _session.PositionUpdateRejections >= 2,
			TimeSpan.FromSeconds(5));
	}

	[Test]
	public void replication_loop_enters_idle_after_the_rejected_position_update()
	{
		AssertEx.IsOrBecomesTrue(
			() => Service.GetStatistics().CurrentIdleTime.HasValue,
			TimeSpan.FromSeconds(1));
	}

	private sealed class RejectingPositionUpdateSession(Guid connectionId) : IReplicationSession
	{
		private int _batchSendAttempts;
		private int _positionUpdateRejections;

		public ReplicationSessionIdentity Identity { get; } =
			ReplicationSessionIdentity.ForInsecureSystem(connectionId);
		public Guid ConnectionId { get; } = connectionId;
		public int SendQueueSize => 0;
		public bool IsClosed => false;
		public int PositionUpdateRejections => Volatile.Read(ref _positionUpdateRejections);
		public long SentReplicationPosition { get; private set; } = long.MaxValue;

		public ReplicationSessionStatistics GetStatistics() => default;

		public ReplicationSendResult TrySend(Message message) => ReplicationSendResult.Sent;

		public ReplicationSendResult TrySend(IReadOnlyList<Message> messages)
		{
			if (Interlocked.Increment(ref _batchSendAttempts) == 1)
			{
				return ReplicationSendResult.Sent;
			}

			Interlocked.Increment(ref _positionUpdateRejections);
			return ReplicationSendResult.QueueFull;
		}

		public void SetSentReplicationPosition(long position) => SentReplicationPosition = position;

		public void Reject(ReplicationSessionRejection rejection)
		{
		}

		public void Close(string reason)
		{
		}
	}
}
