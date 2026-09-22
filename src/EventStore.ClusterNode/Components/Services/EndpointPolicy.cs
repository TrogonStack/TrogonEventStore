using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace EventStore.ClusterNode.Components.Services;

public enum EndpointRole
{
	Client,
	Cluster,
}

public sealed record EndpointBinding(
	EndpointRole Role,
	IPEndPoint ListenEndPoint,
	HttpProtocols Protocols);

public sealed record EndpointRoute(PathString Path, EndpointRole Role);

public sealed class EndpointPolicy
{
	private readonly IReadOnlyList<EndpointBinding> _bindings;
	private readonly IReadOnlyList<EndpointRoute> _routes;
	private readonly EndpointRole _defaultRouteRole;
	private readonly EndpointRole _nonIpEndpointRole;

	public EndpointPolicy(
		IReadOnlyList<EndpointBinding> bindings,
		IReadOnlyList<EndpointRoute> routes,
		EndpointRole defaultRouteRole,
		EndpointRole nonIpEndpointRole)
	{
		_bindings = bindings;
		_routes = routes;
		_defaultRouteRole = defaultRouteRole;
		_nonIpEndpointRole = nonIpEndpointRole;
	}

	public bool Allows(HttpContext context)
	{
		var endpointRole = GetEndpointRole(context.Connection.LocalIpAddress, context.Connection.LocalPort);
		return endpointRole.HasValue && endpointRole.Value == GetRouteRole(context.Request.Path);
	}

	private EndpointRole GetRouteRole(PathString requestPath)
	{
		foreach (var route in _routes)
		{
			if (requestPath.StartsWithSegments(route.Path, StringComparison.Ordinal))
				return route.Role;
		}

		return _defaultRouteRole;
	}

	private EndpointRole? GetEndpointRole(IPAddress localAddress, int localPort)
	{
		if (localAddress is null)
			return _nonIpEndpointRole;

		foreach (var binding in _bindings)
		{
			if (!Matches(binding.ListenEndPoint, localAddress, localPort))
				continue;

			return binding.Role;
		}

		return null;
	}

	private static bool Matches(IPEndPoint listenEndPoint, IPAddress localAddress, int localPort)
	{
		if (localPort != listenEndPoint.Port)
			return false;

		return listenEndPoint.Address.Equals(IPAddress.Any) ||
			listenEndPoint.Address.Equals(IPAddress.IPv6Any) ||
			listenEndPoint.Address.Equals(localAddress);
	}
}
