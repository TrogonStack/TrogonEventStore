using System;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class ReplicationEndpointPolicy(IPEndPoint listenEndPoint)
{
	private static readonly PathString ReplicationServicePath =
		new($"/{EventStore.Replication.Replication.Descriptor.FullName}");
	private static readonly PathString RequestForwardingServicePath =
		new($"/{EventStore.Forwarding.RequestForwarding.Descriptor.FullName}");

	public bool Allows(HttpContext context)
	{
		var isReplicationConnection = Matches(context.Connection.LocalIpAddress, context.Connection.LocalPort);
		var isInternalRequest = context.Request.Path.StartsWithSegments(
			ReplicationServicePath,
			StringComparison.Ordinal) || context.Request.Path.StartsWithSegments(
			RequestForwardingServicePath,
			StringComparison.Ordinal);

		return isReplicationConnection == isInternalRequest;
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
