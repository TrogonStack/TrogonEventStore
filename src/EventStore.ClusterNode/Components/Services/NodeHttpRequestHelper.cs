using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using EventStore.Common.Utils;
using EventStore.Core;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

internal static class NodeHttpRequestHelper {
	public static LocalHttpEndPoint GetLocalEndPoint(StandardComponents standardComponents) {
		var httpServices = standardComponents.HttpServices;
		if (httpServices is null || httpServices.Length == 0)
			throw new InvalidOperationException("Node HTTP endpoint is unavailable.");

		var endPoint = httpServices
			.SelectMany(x => x.EndPoints)
			.FirstOrDefault();

		if (endPoint is null)
			throw new InvalidOperationException("Node HTTP endpoint is unavailable.");

		return new LocalHttpEndPoint(LocalHostFor(endPoint), endPoint.GetPort());
	}

	public static Uri BuildUri(HttpRequest request, LocalHttpEndPoint endPoint, string path, string query) {
		var normalizedPath = path.StartsWith('/') ? path : $"/{path}";
		var builder = new UriBuilder(request.Scheme, endPoint.Host, endPoint.Port) {
			Path = $"{request.PathBase}{normalizedPath}",
			Query = query
		};

		return builder.Uri;
	}

	public static void CopyHeader(HttpRequest source, HttpRequestMessage target, string headerName) {
		if (source.Headers.TryGetValue(headerName, out var value) && value.Count > 0)
			target.Headers.TryAddWithoutValidation(headerName, value.ToArray());
	}

	private static string LocalHostFor(EndPoint endPoint) =>
		endPoint switch {
			IPEndPoint { Address: var address } when IPAddress.Any.Equals(address) => IPAddress.Loopback.ToString(),
			IPEndPoint { Address: var address } when IPAddress.IPv6Any.Equals(address) => IPAddress.Loopback.ToString(),
			IPEndPoint { Address: var address } => address.ToString(),
			DnsEndPoint { Host: var host } when !string.IsNullOrWhiteSpace(host) => host,
			_ => endPoint.GetHost()
		};
}

internal readonly record struct LocalHttpEndPoint(string Host, int Port);
