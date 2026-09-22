using System.Net;
using EventStore.Common.Utils;

namespace EventStore.Core.Data
{
	public class GossipAdvertiseInfo
	{
		public DnsEndPoint InternalTcp { get; }
		public DnsEndPoint InternalSecureTcp { get; }
		public DnsEndPoint ExternalTcp { get; }
		public DnsEndPoint ExternalSecureTcp { get; }
		public DnsEndPoint HttpEndPoint { get; }
		public DnsEndPoint ClusterEndPoint { get; }
		public DnsEndPoint ReplicationEndPoint => ClusterEndPoint;
		public string AdvertiseInternalHostAs { get; }
		public string AdvertiseExternalHostAs { get; }
		public int AdvertiseHttpPortAs { get; }
		public string AdvertiseHostToClientAs { get; }
		public int AdvertiseHttpPortToClientAs { get; }
		public int AdvertiseTcpPortToClientAs { get; }

		public GossipAdvertiseInfo(DnsEndPoint internalTcp, DnsEndPoint internalSecureTcp,
			DnsEndPoint externalTcp, DnsEndPoint externalSecureTcp,
			DnsEndPoint httpEndPoint,
			string advertiseInternalHostAs, string advertiseExternalHostAs, int advertiseHttpPortAs,
			string advertiseHostToClientAs, int advertiseHttpPortToClientAs, int advertiseTcpPortToClientAs,
			DnsEndPoint clusterEndPoint = null)
		{
			Ensure.Equal(false, internalTcp == null && internalSecureTcp == null, "Both internal TCP endpoints are null");

			InternalTcp = internalTcp;
			InternalSecureTcp = internalSecureTcp;
			ExternalTcp = externalTcp;
			ExternalSecureTcp = externalSecureTcp;
			HttpEndPoint = httpEndPoint;
			ClusterEndPoint = clusterEndPoint ?? httpEndPoint;
			AdvertiseInternalHostAs = advertiseInternalHostAs;
			AdvertiseExternalHostAs = advertiseExternalHostAs;
			AdvertiseHttpPortAs = advertiseHttpPortAs;
			AdvertiseHostToClientAs = advertiseHostToClientAs;
			AdvertiseHttpPortToClientAs = advertiseHttpPortToClientAs;
			AdvertiseTcpPortToClientAs = advertiseTcpPortToClientAs;
		}

		public override string ToString()
		{
			return string.Format(
				$"IntTcp: {InternalTcp}, IntSecureTcp: {InternalSecureTcp}\n" +
				$"ExtTcp: {ExternalTcp}, ExtSecureTcp: {ExternalSecureTcp}\n" +
				$"Http: {HttpEndPoint}, Cluster: {ClusterEndPoint}, HttpAdvertiseAs: {AdvertiseExternalHostAs}:{AdvertiseHttpPortAs},\n" +
				$"HttpAdvertiseToClientAs: {AdvertiseHostToClientAs}:{AdvertiseHttpPortToClientAs},\n" +
				$"TcpAdvertiseToClientAs: {AdvertiseHostToClientAs}:{AdvertiseTcpPortToClientAs}");
		}
	}
}
