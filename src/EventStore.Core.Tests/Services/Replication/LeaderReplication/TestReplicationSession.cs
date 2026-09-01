using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Replication;

namespace EventStore.Core.Tests.Services.Replication.LeaderReplication;

public sealed class TestReplicationSession(
	Guid connectionId,
	ReplicationSessionStatistics statistics = default,
	ReplicationSessionIdentity identity = null) : IReplicationSession
{
	private int _isClosed;

	public ConcurrentQueue<Message> Messages { get; } = new();
	public ReplicationSessionIdentity Identity { get; } =
		identity ?? ReplicationSessionIdentity.ForInsecureSystem(connectionId);
	public Guid ConnectionId { get; } = connectionId;
	public long SentReplicationPosition { get; private set; } = long.MaxValue;
	public int SendQueueSize => statistics.SendQueueSize;
	public bool IsClosed => Volatile.Read(ref _isClosed) != 0;

	public ReplicationSessionStatistics GetStatistics() => statistics;

	public ReplicationSendResult TrySend(Message message)
	{
		Messages.Enqueue(message);
		return ReplicationSendResult.Sent;
	}

	public ReplicationSendResult TrySend(IReadOnlyList<Message> messages)
	{
		foreach (var message in messages)
		{
			Messages.Enqueue(message);
		}
		return ReplicationSendResult.Sent;
	}

	public void SetSentReplicationPosition(long position) => SentReplicationPosition = position;

	public void Reject(ReplicationSessionRejection rejection) => Close(rejection.Reason);

	public void Close(string reason) => Interlocked.Exchange(ref _isClosed, 1);
}
