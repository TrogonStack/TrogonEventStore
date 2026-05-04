using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Services.Transport.Http.NodeHttpClientFactory;
using EventStore.Plugins.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace EventStore.ClusterNode.Components.Services;

public sealed class NodeProbeService : IDisposable {
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

	public static readonly IReadOnlyList<NodeProbeDefinition> Probes = [
		new(
			"ping",
			"Ping",
			"/ping",
			"Process availability probe.",
			new Operation(Operations.Node.Ping)),
		new(
			"info",
			"Node info",
			"",
			"Version, state, feature, and authentication metadata.",
			new Operation(Operations.Node.Information.Read),
			NodeProbeKind.NodeInformation),
		new(
			"gossip",
			"Gossip",
			"/gossip",
			"Current cluster membership view.",
			new Operation(Operations.Node.Gossip.ClientRead))
	];

	private static readonly JsonSerializerOptions IndentedJson = new() {
		WriteIndented = true
	};

	private readonly IAuthorizationProvider _authorizationProvider;
	private readonly IHttpContextAccessor _httpContextAccessor;
	private readonly NodeInformationProvider _nodeInformationProvider;
	private readonly HttpClient _client;
	private readonly LocalHttpEndPoint _nodeEndPoint;

	public NodeProbeService(
		IAuthorizationProvider authorizationProvider,
		IHttpContextAccessor httpContextAccessor,
		NodeInformationProvider nodeInformationProvider,
		INodeHttpClientFactory nodeHttpClientFactory,
		StandardComponents standardComponents) {
		_authorizationProvider = authorizationProvider;
		_httpContextAccessor = httpContextAccessor;
		_nodeInformationProvider = nodeInformationProvider;
		_nodeEndPoint = NodeHttpRequestHelper.GetLocalEndPoint(standardComponents);
		_client = nodeHttpClientFactory.CreateHttpClient([_nodeEndPoint.Host]);
	}

	public async Task<NodeProbeRead> Read(string key, CancellationToken cancellationToken = default) {
		var probe = Find(key);
		if (probe is null)
			return NodeProbeRead.Unselected();

		if (!await HasAccess(probe.Operation, cancellationToken))
			return NodeProbeRead.Unavailable(probe, $"{probe.Title} access was denied.");

		if (probe.Kind == NodeProbeKind.NodeInformation)
			return NodeProbeRead.Available(probe, FormatPayload(_nodeInformationProvider.ReadJson()));

		try {
			var context = _httpContextAccessor.HttpContext;
			if (context is null)
				return NodeProbeRead.Unavailable(probe, $"{probe.Title} is unavailable outside an HTTP request.");

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(ReadTimeout);
			using var request = new HttpRequestMessage(
				HttpMethod.Get,
				NodeHttpRequestHelper.BuildUri(context.Request, _nodeEndPoint, probe.Path, "format=json"));
			NodeHttpRequestHelper.CopyHeader(context.Request, request, HeaderNames.Authorization);
			NodeHttpRequestHelper.CopyHeader(context.Request, request, HeaderNames.Cookie);

			using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
				return NodeProbeRead.Unavailable(probe, $"{probe.Title} access was denied.");

			if (!response.IsSuccessStatusCode)
				return NodeProbeRead.Unavailable(
					probe,
					$"{probe.Title} endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}.");

			var content = await response.Content.ReadAsStringAsync(timeout.Token);
			return NodeProbeRead.Available(probe, FormatPayload(content));
		} catch (TimeoutException) {
			return NodeProbeRead.Unavailable(probe, $"Timed out reading {probe.Title.ToLowerInvariant()}.");
		} catch (OperationCanceledException) {
			if (cancellationToken.IsCancellationRequested)
				throw;

			return NodeProbeRead.Unavailable(probe, $"Timed out reading {probe.Title.ToLowerInvariant()}.");
		} catch (Exception ex) {
			return NodeProbeRead.Unavailable(probe, $"Unable to read {probe.Title.ToLowerInvariant()}: {UiMessages.Friendly(ex)}");
		}
	}

	public void Dispose() =>
		_client.Dispose();

	public static NodeProbeDefinition Find(string key) =>
		Probes.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));

	private ClaimsPrincipal CurrentUser =>
		_httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private Task<bool> HasAccess(Operation operation, CancellationToken cancellationToken) =>
		_authorizationProvider.CheckAccessAsync(CurrentUser, operation, cancellationToken).AsTask();

	private static string FormatPayload(string content) {
		if (string.IsNullOrWhiteSpace(content))
			return "";

		try {
			using var document = JsonDocument.Parse(content);
			return JsonSerializer.Serialize(document.RootElement, IndentedJson);
		} catch (JsonException) {
			return content;
		}
	}
}

public sealed record NodeProbeDefinition(
	string Key,
	string Title,
	string Path,
	string Description,
	Operation Operation,
	NodeProbeKind Kind = NodeProbeKind.Http);

public enum NodeProbeKind {
	Http,
	NodeInformation
}

public sealed record NodeProbeRead(
	NodeProbeDefinition Probe,
	string Content,
	string Message) {
	public bool HasProbe => Probe is not null;
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);

	public static NodeProbeRead Unselected() => new(null, "", "");

	public static NodeProbeRead Available(NodeProbeDefinition probe, string content) =>
		new(probe, content, "");

	public static NodeProbeRead Unavailable(NodeProbeDefinition probe, string message) =>
		new(probe, "", message);
}
