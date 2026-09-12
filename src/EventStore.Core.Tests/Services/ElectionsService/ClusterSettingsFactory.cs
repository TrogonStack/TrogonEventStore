using System;
using System.Linq;
using System.Net;
using EventStore.Core.Cluster.Settings;

namespace EventStore.Core.Tests.Services.ElectionsService;

public class ClusterSettingsFactory
{
	private const int ManagerPort = 1001;
	private const int StartingPort = 1002;

	private static ClusterVNodeSettings CreateVNode(int nodeNumber, bool isReadOnlyReplica)
	{
		var httpPort = StartingPort + nodeNumber;

		return new ClusterVNodeSettings(Guid.NewGuid(), 0,
			GetLoopbackForPort(httpPort), 0,
			isReadOnlyReplica);
	}

	private static IPEndPoint GetLoopbackForPort(int port) => new(IPAddress.Loopback, port);

	public static ClusterSettings GetClusterSettings(int selfIndex, int nodesCount, bool isSelfReadOnlyReplica)
	{
		if (selfIndex < 0 || selfIndex >= nodesCount)
		{
			throw new ArgumentOutOfRangeException("selfIndex", "Index of self should be in range of created nodes");
		}

		var clusterManager = GetLoopbackForPort(ManagerPort);
		var nodes = Enumerable.Range(0, nodesCount).Select(x =>
			x == selfIndex ? CreateVNode(x, isSelfReadOnlyReplica) : CreateVNode(x, false)).ToArray();

		var self = nodes[selfIndex];
		var others = nodes.Where((_, i) => i != selfIndex).ToArray();

		return new ClusterSettings("test-dns", clusterManager, self, others, nodes.Length);
	}
}
