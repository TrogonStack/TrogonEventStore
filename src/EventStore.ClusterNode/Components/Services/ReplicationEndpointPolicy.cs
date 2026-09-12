using System;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class ReplicationEndpointPolicy(IPEndPoint listenEndPoint)
{
	private const string ReplicationServicePath = "/event_store.replication.Replication";

	public bool Allows(HttpContext context)
	{
		var isReplicationConnection = Matches(context.Connection.LocalIpAddress, context.Connection.LocalPort);
		var isReplicationRequest = context.Request.Path.StartsWithSegments(
			ReplicationServicePath,
			StringComparison.Ordinal);

		return isReplicationConnection == isReplicationRequest;
	}

	private bool Matches(IPAddress localAddress, int localPort)
	{
		if (localPort != listenEndPoint.Port)
		{
			return false;
		}

		return listenEndPoint.Address.Equals(IPAddress.Any) ||
			listenEndPoint.Address.Equals(IPAddress.IPv6Any) ||
			listenEndPoint.Address.Equals(localAddress);
	}
}
