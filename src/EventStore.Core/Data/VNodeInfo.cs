using System;
using System.Net;
using EventStore.Common.Utils;

namespace EventStore.Core.Data
{
	public class VNodeInfo
	{
		public readonly Guid InstanceId;
		public readonly int DebugIndex;
		public readonly EndPoint HttpEndPoint;
		public readonly bool IsReadOnlyReplica;

		public VNodeInfo(Guid instanceId, int debugIndex, EndPoint httpEndPoint,
			bool isReadOnlyReplica)
		{
			Ensure.NotEmptyGuid(instanceId, "instanceId");
			Ensure.NotNull(httpEndPoint, nameof(httpEndPoint));

			DebugIndex = debugIndex;
			InstanceId = instanceId;
			HttpEndPoint = httpEndPoint;
			IsReadOnlyReplica = isReadOnlyReplica;
		}

		public bool Is(EndPoint endPoint)
		{
			return endPoint != null && HttpEndPoint.Equals(endPoint);
		}

		public override string ToString()
		{
			return $"InstanceId: {InstanceId:B}, HttpEndPoint: {HttpEndPoint}, IsReadOnlyReplica: {IsReadOnlyReplica}";
		}
	}
}
