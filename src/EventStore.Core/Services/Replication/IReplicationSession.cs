using System;
using System.Collections.Generic;
using EventStore.Core.Messaging;

namespace EventStore.Core.Services.Replication;

public readonly record struct ReplicationSessionStatistics(
	int SendQueueSize,
	long TotalBytesSent,
	long TotalBytesReceived,
	int PendingSendBytes,
	int PendingReceivedBytes);

public readonly record struct ReplicationSessionRejection(Guid CorrelationId, string Reason);

public enum ReplicationSendResult
{
	Sent,
	QueueFull,
	Closed
}

public interface IReplicationSession
{
	ReplicationSessionIdentity Identity { get; }
	Guid ConnectionId { get; }
	long SentReplicationPosition { get; }
	int SendQueueSize { get; }
	bool IsClosed { get; }
	ReplicationSessionStatistics GetStatistics();
	ReplicationSendResult TrySend(Message message);
	ReplicationSendResult TrySend(IReadOnlyList<Message> messages);
	void Reject(ReplicationSessionRejection rejection);
	void Close(string reason);
}
