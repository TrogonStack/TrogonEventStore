using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core;
using EventStore.Core.Cluster;
using EventStore.Core.Messages;
using EventStore.Plugins.Authorization;

namespace EventStore.ClusterNode.Components.Services;

public sealed class ClusterStatusService(
	IAuthorizationProvider authorizationProvider,
	StandardComponents standardComponents) {
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(8);
	private static readonly Operation ReadOperation = new(Operations.Node.Gossip.ClientRead);

	public async Task<ClientClusterInfo> Read(ClaimsPrincipal user, CancellationToken cancellationToken = default) {
		if (!await authorizationProvider.CheckAccessAsync(user, ReadOperation, cancellationToken))
			throw new UnauthorizedAccessException("Cluster membership access was denied.");

		var envelope = new TaskCompletionEnvelope<GossipMessage.SendClientGossip>();
		standardComponents.MainQueue.Publish(new GossipMessage.ClientGossip(envelope));
		var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		return completed.ClusterInfo;
	}
}
