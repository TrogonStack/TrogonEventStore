using System.Net;
using EventStore.Common.Utils;

namespace EventStore.Core.Data
{
	public class GossipAdvertiseInfo
	{
		public DnsEndPoint ReplicationEndPoint { get; }
		public DnsEndPoint HttpEndPoint { get; }
		public string AdvertiseHostToClientAs { get; }
		public int AdvertiseHttpPortToClientAs { get; }

		public GossipAdvertiseInfo(DnsEndPoint httpEndPoint,
			string advertiseHostToClientAs, int advertiseHttpPortToClientAs,
			DnsEndPoint replicationEndPoint = null)
		{
			Ensure.NotNull(httpEndPoint, nameof(httpEndPoint));
			ReplicationEndPoint = replicationEndPoint ?? httpEndPoint;
			HttpEndPoint = httpEndPoint;
			AdvertiseHostToClientAs = advertiseHostToClientAs;
			AdvertiseHttpPortToClientAs = advertiseHttpPortToClientAs;
		}

		public override string ToString()
		{
			return $"Replication: {ReplicationEndPoint}, Http: {HttpEndPoint}, HttpAdvertiseToClientAs: {AdvertiseHostToClientAs}:{AdvertiseHttpPortToClientAs}";
		}
	}
}
