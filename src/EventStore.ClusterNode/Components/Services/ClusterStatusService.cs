using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Services.Transport.Http.NodeHttpClientFactory;
using EventStore.Plugins.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace EventStore.ClusterNode.Components.Services;

public sealed class ClusterStatusService(
	IAuthorizationProvider authorizationProvider,
	IHttpContextAccessor httpContextAccessor,
	INodeHttpClientFactory nodeHttpClientFactory,
	StandardComponents standardComponents) : IDisposable {
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(8);
	private static readonly Operation ReadOperation = new(Operations.Node.Gossip.ClientRead);
	private static readonly Operation ReplicationOperation = new(Operations.Node.Statistics.Replication);
	private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
	private readonly object _replicaGate = new();
	private Dictionary<Guid, ClusterReplicaRow> _previousReplicas = new();
	private string _previousLeaderEndpoint = "";

	public Task<ClientClusterInfo> Read(CancellationToken cancellationToken = default) =>
		Read(CurrentUser, cancellationToken);

	public async Task<ClientClusterInfo> Read(ClaimsPrincipal user, CancellationToken cancellationToken = default) {
		if (!await authorizationProvider.CheckAccessAsync(user, ReadOperation, cancellationToken))
			throw new UnauthorizedAccessException("Cluster membership access was denied.");

		var envelope = new TaskCompletionEnvelope<GossipMessage.SendClientGossip>();
		standardComponents.MainQueue.Publish(new GossipMessage.ClientGossip(envelope));
		var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		return completed.ClusterInfo;
	}

	public async Task<ClusterReplicaPage> ReadReplicas(
		ClientClusterInfo clusterInfo,
		CancellationToken cancellationToken = default) {
		if (!await authorizationProvider.CheckAccessAsync(CurrentUser, ReplicationOperation, cancellationToken))
			return ClusterReplicaPage.Unavailable("Replica statistics access was denied.");

		var leader = clusterInfo?.Members?.FirstOrDefault(x => x.State == VNodeState.Leader);
		if (leader is null)
			return ClusterReplicaPage.Unavailable("Replica stats are unavailable until a leader is known.");

		var context = httpContextAccessor.HttpContext;
		if (context is null)
			return ClusterReplicaPage.Unavailable("Replica stats are unavailable outside an HTTP request.");

		var leaderEndpoint = HttpEndpoint(leader);
		if (string.IsNullOrWhiteSpace(leader.HttpEndPointIp) || leader.HttpEndPointPort <= 0)
			return ClusterReplicaPage.Unavailable("Leader HTTP endpoint is unavailable.");

		try {
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(ReadTimeout);
			using var request = new HttpRequestMessage(
				HttpMethod.Get,
				BuildLeaderUri(context.Request, leader, "/stats/replication", "format=json"));
			NodeHttpRequestHelper.CopyHeader(context.Request, request, HeaderNames.Authorization);
			NodeHttpRequestHelper.CopyHeader(context.Request, request, HeaderNames.Cookie);

			using var response = await ClientFor(leader.HttpEndPointIp).SendAsync(
				request,
				HttpCompletionOption.ResponseHeadersRead,
				timeout.Token);

			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
				return ClusterReplicaPage.Unavailable("Replica statistics access was denied.");

			if (!response.IsSuccessStatusCode)
				return ClusterReplicaPage.Unavailable(
					$"Replica stats endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}.");

			await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
			using var document = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);
			return ParseReplicaStats(document, clusterInfo.Members, leader, leaderEndpoint, DateTime.UtcNow);
		} catch (OperationCanceledException) {
			if (cancellationToken.IsCancellationRequested)
				throw;

			return ClusterReplicaPage.Unavailable("Timed out reading replica statistics.");
		} catch (Exception ex) {
			return ClusterReplicaPage.Unavailable($"Unable to read replica statistics: {UiMessages.Friendly(ex)}");
		}
	}

	private HttpClient ClientFor(string host) =>
		_clients.GetOrAdd(host, static (value, factory) => factory.CreateHttpClient(new[] { value }), nodeHttpClientFactory);

	public void Dispose() {
		foreach (var client in _clients.Values)
			client.Dispose();
	}

	private ClusterReplicaPage ParseReplicaStats(
		JsonDocument document,
		IReadOnlyList<ClientClusterInfo.ClientMemberInfo> members,
		ClientClusterInfo.ClientMemberInfo leader,
		string leaderEndpoint,
		DateTime now) {
		if (document.RootElement.ValueKind != JsonValueKind.Array)
			return ClusterReplicaPage.Unavailable("Replica statistics are unavailable.");

		lock (_replicaGate) {
			if (!string.Equals(_previousLeaderEndpoint, leaderEndpoint, StringComparison.OrdinalIgnoreCase)) {
				_previousLeaderEndpoint = leaderEndpoint;
				_previousReplicas = new Dictionary<Guid, ClusterReplicaRow>();
			}

			var rows = document.RootElement
				.EnumerateArray()
				.Where(x => x.ValueKind == JsonValueKind.Object)
				.Select(x => ParseReplicaRow(x, members, leader, now))
				.ToArray();

			_previousReplicas = rows.ToDictionary(x => x.ConnectionId);
			return ClusterReplicaPage.Success(rows, now);
		}
	}

	private ClusterReplicaRow ParseReplicaRow(
		JsonElement row,
		IReadOnlyList<ClientClusterInfo.ClientMemberInfo> members,
		ClientClusterInfo.ClientMemberInfo leader,
		DateTime now) {
		var connectionId = ReadGuid(row, "connectionId", "ConnectionId");
		var totalBytesSent = ReadLong(row, "totalBytesSent", "TotalBytesSent");
		var previousRow = _previousReplicas.GetValueOrDefault(connectionId);
		var replicaNode = FindMemberByInternalEndpoint(members, ReadString(row, "subscriptionEndpoint", "SubscriptionEndpoint"));
		var isCatchingUp = replicaNode?.State == VNodeState.CatchingUp;
		var catchupStartTime = now;
		var catchupStartBytesSent = totalBytesSent;

		if (previousRow?.IsCatchingUp == true) {
			catchupStartTime = previousRow.CatchupStartTime;
			catchupStartBytesSent = previousRow.CatchupStartBytesSent;
		}

		var catchupIntervals = Math.Max(1, (now - catchupStartTime).TotalSeconds);
		var approximateSpeed = (long)Math.Round(Math.Max(0, totalBytesSent - catchupStartBytesSent) / catchupIntervals);
		var bytesToCatchUp = isCatchingUp && replicaNode is not null
			? Math.Max(0, leader.WriterCheckpoint - replicaNode.WriterCheckpoint)
			: 0;

		return new ClusterReplicaRow(
			connectionId,
			ReadString(row, "subscriptionEndpoint", "SubscriptionEndpoint", "<none>"),
			totalBytesSent,
			ReadLong(row, "totalBytesReceived", "TotalBytesReceived"),
			ReadInt(row, "pendingSendBytes", "PendingSendBytes"),
			ReadInt(row, "pendingReceivedBytes", "PendingReceivedBytes"),
			ReadInt(row, "sendQueueSize", "SendQueueSize"),
			isCatchingUp,
			bytesToCatchUp,
			approximateSpeed,
			catchupStartTime,
			catchupStartBytesSent);
	}

	private ClaimsPrincipal CurrentUser =>
		httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private static ClientClusterInfo.ClientMemberInfo FindMemberByInternalEndpoint(
		IReadOnlyList<ClientClusterInfo.ClientMemberInfo> members,
		string endpoint) {
		var cleaned = endpoint.Replace("Unspecified/", "", StringComparison.OrdinalIgnoreCase);
		return members.FirstOrDefault(x => string.Equals(InternalTcpEndpoint(x), cleaned, StringComparison.OrdinalIgnoreCase));
	}

	private static Uri BuildLeaderUri(
		HttpRequest request,
		ClientClusterInfo.ClientMemberInfo leader,
		string path,
		string query) {
		var normalizedPath = path.StartsWith('/') ? path : $"/{path}";
		var builder = new UriBuilder(request.Scheme, leader.HttpEndPointIp, leader.HttpEndPointPort) {
			Path = $"{request.PathBase}{normalizedPath}",
			Query = query
		};

		return builder.Uri;
	}

	private static string InternalTcpEndpoint(ClientClusterInfo.ClientMemberInfo member) =>
		Endpoint(
			member.InternalTcpIp,
			member.InternalSecureTcpPort != 0 ? member.InternalSecureTcpPort : member.InternalTcpPort);

	private static string HttpEndpoint(ClientClusterInfo.ClientMemberInfo member) =>
		Endpoint(member.HttpEndPointIp, member.HttpEndPointPort);

	private static string Endpoint(string host, int port) =>
		$"{(string.IsNullOrWhiteSpace(host) ? "<none>" : host)}:{port}";

	private static string ReadString(JsonElement row, string camelName, string pascalName, string fallback = "") {
		if (!TryGetProperty(row, camelName, pascalName, out var value))
			return fallback;

		return value.ValueKind switch {
			JsonValueKind.String => value.GetString() ?? fallback,
			JsonValueKind.Null => fallback,
			JsonValueKind.Undefined => fallback,
			_ => value.ToString()
		};
	}

	private static int ReadInt(JsonElement row, string camelName, string pascalName) {
		if (!TryGetProperty(row, camelName, pascalName, out var value))
			return 0;

		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
			return number;

		return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: 0;
	}

	private static long ReadLong(JsonElement row, string camelName, string pascalName) {
		if (!TryGetProperty(row, camelName, pascalName, out var value))
			return 0;

		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
			return number;

		return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: 0;
	}

	private static Guid ReadGuid(JsonElement row, string camelName, string pascalName) {
		var value = ReadString(row, camelName, pascalName);
		return Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;
	}

	private static bool TryGetProperty(JsonElement row, string camelName, string pascalName, out JsonElement value) =>
		row.TryGetProperty(camelName, out value) || row.TryGetProperty(pascalName, out value);
}

public sealed record ClusterReplicaPage(
	IReadOnlyList<ClusterReplicaRow> Replicas,
	string Message,
	DateTime? ReadAt) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public string StatusLabel => Replicas.Count == 0
		? string.IsNullOrWhiteSpace(Message) ? "No replica stats reported." : Message
		: $"{Replicas.Count} replica connection{(Replicas.Count == 1 ? "" : "s")}";

	public static ClusterReplicaPage Success(IReadOnlyList<ClusterReplicaRow> replicas, DateTime readAt) =>
		new(replicas, "", readAt);

	public static ClusterReplicaPage Unavailable(string message) =>
		new(Array.Empty<ClusterReplicaRow>(), message, null);
}

public sealed record ClusterReplicaRow(
	Guid ConnectionId,
	string SubscriptionEndpoint,
	long TotalBytesSent,
	long TotalBytesReceived,
	int PendingSendBytes,
	int PendingReceivedBytes,
	int SendQueueSize,
	bool IsCatchingUp,
	long BytesToCatchUp,
	long ApproximateSpeed,
	DateTime CatchupStartTime,
	long CatchupStartBytesSent) {
	public string TotalBytesSentLabel => FormatInteger(TotalBytesSent);
	public string TotalBytesReceivedLabel => FormatInteger(TotalBytesReceived);
	public string PendingSendBytesLabel => FormatInteger(PendingSendBytes);
	public string PendingReceivedBytesLabel => FormatInteger(PendingReceivedBytes);
	public string SendQueueSizeLabel => FormatInteger(SendQueueSize);
	public string BytesToCatchUpLabel => FormatInteger(BytesToCatchUp);
	public string ApproximateSpeedLabel => FormatInteger(ApproximateSpeed);
	public string EstimatedTimeLabel => IsCatchingUp && ApproximateSpeed > 0
		? FormatDuration(Math.Max(1, (long)Math.Round(BytesToCatchUp / (double)ApproximateSpeed)))
		: "-";

	private static string FormatInteger(long value) =>
		value.ToString("N0", CultureInfo.InvariantCulture);

	private static string FormatDuration(long totalSeconds) {
		var time = TimeSpan.FromSeconds(totalSeconds);
		return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
	}
}
