using System;
using System.Linq;
using System.Net;
using EventStore.Common.Utils;
using EventStore.Core.Data;
using EventStore.Core.TransactionLog.Chunks;

namespace EventStore.Core.Tests.Services.ElectionsService;

public class ClusterVNodeSettings
{
	public readonly VNodeInfo NodeInfo;

	public readonly int NodePriority;

	public readonly bool ReadOnlyReplica;

	public ClusterVNodeSettings(Guid instanceId, int debugIndex,
		IPEndPoint httpEndPoint,
		int nodePriority,
		bool readOnlyReplica)
	{
		Ensure.NotEmptyGuid(instanceId, "instanceId");
		Ensure.NotNull(httpEndPoint, nameof(httpEndPoint));

		NodeInfo = new VNodeInfo(instanceId, debugIndex, httpEndPoint, readOnlyReplica);


		NodePriority = nodePriority;
		ReadOnlyReplica = readOnlyReplica;
	}
}
