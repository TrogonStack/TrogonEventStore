using System;
using System.Collections.Generic;

namespace EventStore.Core.Services.Transport.Grpc;

public interface IConnectionStatsProvider
{
	IReadOnlyList<ConnectionStatsSnapshot> Snapshot();
}

public record ConnectionStatsSnapshot(
	string ConnectionId,
	string RemoteEndPoint,
	string LocalEndPoint,
	string ClientName,
	string Application,
	string Protocol,
	bool IsTls,
	DateTimeOffset ConnectedAt,
	long TotalBytesSent,
	long TotalBytesReceived,
	long PendingSendBytes,
	long PendingReceivedBytes);

internal sealed class EmptyConnectionStatsProvider : IConnectionStatsProvider
{
	public static readonly EmptyConnectionStatsProvider Instance = new();

	public IReadOnlyList<ConnectionStatsSnapshot> Snapshot() => Array.Empty<ConnectionStatsSnapshot>();
}
