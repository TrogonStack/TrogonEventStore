using System.Net;
using EventStore.Common.Utils;

namespace EventStore.Core.Data
{
	public class GossipAdvertiseInfo
	{
		public DnsEndPoint HttpEndPoint { get; }
		public DnsEndPoint ClusterEndPoint { get; }
		public DnsEndPoint ReplicationEndPoint => ClusterEndPoint;
		public string AdvertiseHostToClientAs { get; }
		public int AdvertiseHttpPortToClientAs { get; }

		public GossipAdvertiseInfo(DnsEndPoint httpEndPoint,
			string advertiseHostToClientAs, int advertiseHttpPortToClientAs,
			DnsEndPoint clusterEndPoint = null)
		{
			Ensure.NotNull(httpEndPoint, nameof(httpEndPoint));
			HttpEndPoint = httpEndPoint;
			ClusterEndPoint = clusterEndPoint ?? httpEndPoint;
			AdvertiseHostToClientAs = advertiseHostToClientAs;
			AdvertiseHttpPortToClientAs = advertiseHttpPortToClientAs;
		}

		public override string ToString()
		{
			return $"Cluster: {ClusterEndPoint}, Http: {HttpEndPoint}, HttpAdvertiseToClientAs: {AdvertiseHostToClientAs}:{AdvertiseHttpPortToClientAs}";
		}
	}
}
