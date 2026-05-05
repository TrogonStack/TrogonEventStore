using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Monitoring;
using EventStore.Core;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Services.Transport.Http.NodeHttpClientFactory;
using EventStore.Plugins.Authorization;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Http;
using MonitoringClient = EventStore.Client.Monitoring.Monitoring.MonitoringClient;

namespace EventStore.ClusterNode.Components.Services;

public sealed class ClusterStatusService(
	IAuthorizationProvider authorizationProvider,
	IHttpContextAccessor httpContextAccessor,
	INodeHttpClientFactory nodeHttpClientFactory,
	StandardComponents standardComponents) : IDisposable {
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(8);
	private static readonly Operation ReadOperation = new(Operations.Node.Gossip.ClientRead);
	private static readonly Operation ReplicationOperation = new(Operations.Node.Statistics.Replication);
	private readonly ConcurrentDictionary<string, LeaderMonitoringClient> _clients = new(StringComparer.OrdinalIgnoreCase);
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

		var leaderEndpoint = HttpEndpoint(leader);
		var context = httpContextAccessor.HttpContext;
		if (context is null)
			return ClusterReplicaPage.Unavailable("Replica stats are unavailable outside an HTTP request.");

		if (string.IsNullOrWhiteSpace(leader.HttpEndPointIp) || leader.HttpEndPointPort <= 0)
			return ClusterReplicaPage.Unavailable("Leader HTTP endpoint is unavailable.");

		try {
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(ReadTimeout);
			var response = await ClientFor(context.Request, leader).Client.ReplicationStatsAsync(
				new ReplicationStatsReq(),
				cancellationToken: timeout.Token);
			return ParseReplicaStats(response, clusterInfo.Members, leader, leaderEndpoint, DateTime.UtcNow);
		} catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied) {
			return ClusterReplicaPage.Unavailable("Replica statistics access was denied.");
		} catch (OperationCanceledException) {
			if (cancellationToken.IsCancellationRequested)
				throw;

			return ClusterReplicaPage.Unavailable("Timed out reading replica statistics.");
		} catch (Exception ex) {
			return ClusterReplicaPage.Unavailable($"Unable to read replica statistics: {UiMessages.Friendly(ex)}");
		}
	}

	private LeaderMonitoringClient ClientFor(
		HttpRequest request,
		ClientClusterInfo.ClientMemberInfo leader) {
		var key = $"{request.Scheme}://{HttpEndpoint(leader)}";
		return _clients.GetOrAdd(
			key,
			static (_, state) => LeaderMonitoringClient.Create(
				BuildLeaderAddress(state.Request, state.Leader),
				state.Factory.CreateHttpClient([state.Leader.HttpEndPointIp])),
			(Request: request, Leader: leader, Factory: nodeHttpClientFactory));
	}

	public void Dispose() {
		foreach (var client in _clients.Values)
			client.Dispose();
	}

	private ClusterReplicaPage ParseReplicaStats(
		ReplicationStatsResp response,
		IReadOnlyList<ClientClusterInfo.ClientMemberInfo> members,
		ClientClusterInfo.ClientMemberInfo leader,
		string leaderEndpoint,
		DateTime now) {
		lock (_replicaGate) {
			if (!string.Equals(_previousLeaderEndpoint, leaderEndpoint, StringComparison.OrdinalIgnoreCase)) {
				_previousLeaderEndpoint = leaderEndpoint;
				_previousReplicas = new Dictionary<Guid, ClusterReplicaRow>();
			}

			var rows = response.Stats
				.Select(x => ParseReplicaRow(x, members, leader, now))
				.ToArray();

			_previousReplicas = rows.ToDictionary(x => x.ConnectionId);
			return ClusterReplicaPage.Success(rows, now);
		}
	}

	private ClusterReplicaRow ParseReplicaRow(
		ReplicationStats row,
		IReadOnlyList<ClientClusterInfo.ClientMemberInfo> members,
		ClientClusterInfo.ClientMemberInfo leader,
		DateTime now) {
		var connectionId = Guid.TryParse(row.ConnectionId, out var parsedConnectionId)
			? parsedConnectionId
			: Guid.Empty;
		var totalBytesSent = row.TotalBytesSent;
		var previousRow = _previousReplicas.GetValueOrDefault(connectionId);
		var replicaNode = FindMemberByInternalEndpoint(members, row.SubscriptionEndpoint);
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
			string.IsNullOrWhiteSpace(row.SubscriptionEndpoint) ? "<none>" : row.SubscriptionEndpoint,
			totalBytesSent,
			row.TotalBytesReceived,
			row.PendingSendBytes,
			row.PendingReceivedBytes,
			row.SendQueueSize,
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

	private static Uri BuildLeaderAddress(
		HttpRequest request,
		ClientClusterInfo.ClientMemberInfo leader) =>
		new UriBuilder(request.Scheme, leader.HttpEndPointIp, leader.HttpEndPointPort).Uri;

	private static string InternalTcpEndpoint(ClientClusterInfo.ClientMemberInfo member) =>
		Endpoint(
			member.InternalTcpIp,
			member.InternalSecureTcpPort != 0 ? member.InternalSecureTcpPort : member.InternalTcpPort);

	private static string HttpEndpoint(ClientClusterInfo.ClientMemberInfo member) =>
		Endpoint(member.HttpEndPointIp, member.HttpEndPointPort);

	private static string Endpoint(string host, int port) =>
		$"{(string.IsNullOrWhiteSpace(host) ? "<none>" : host)}:{port}";

	private sealed class LeaderMonitoringClient : IDisposable {
		private readonly GrpcChannel _channel;
		private readonly HttpClient _httpClient;

		private LeaderMonitoringClient(Uri address, HttpClient httpClient) {
			_httpClient = httpClient;
			_channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions {
				HttpClient = _httpClient,
				DisposeHttpClient = false
			});
			Client = new MonitoringClient(_channel);
		}

		public MonitoringClient Client { get; }

		public static LeaderMonitoringClient Create(Uri address, HttpClient httpClient) =>
			new(address, httpClient);

		public void Dispose() {
			_channel.Dispose();
			_httpClient.Dispose();
		}
	}
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
