using System.Net;
using EventStore.Common.Utils;

namespace EventStore.Core.Data
{
	public class GossipAdvertiseInfo
	{
		public DnsEndPoint HttpEndPoint { get; }
		public string AdvertiseHostToClientAs { get; }
		public int AdvertiseHttpPortToClientAs { get; }

		public GossipAdvertiseInfo(DnsEndPoint httpEndPoint,
			string advertiseHostToClientAs, int advertiseHttpPortToClientAs)
		{
			Ensure.NotNull(httpEndPoint, nameof(httpEndPoint));
			HttpEndPoint = httpEndPoint;
			AdvertiseHostToClientAs = advertiseHostToClientAs;
			AdvertiseHttpPortToClientAs = advertiseHttpPortToClientAs;
		}

		public override string ToString()
		{
			return $"Http: {HttpEndPoint}, HttpAdvertiseToClientAs: {AdvertiseHostToClientAs}:{AdvertiseHttpPortToClientAs}";
		}
	}
}
