using System;
using System.Net;
using System.Reflection;
using EventStore.Core.Data;
using NUnit.Framework;

namespace EventStore.Core.Tests.Cluster;

[TestFixture]
public class MemberInfoTests
{
	[Test]
	public void member_with_dns_endpoint_should_equal()
	{
		var ipAddress = "127.0.0.1";
		var port = 1113;
		var memberWithDnsEndPoint = EventStore.Core.Cluster.MemberInfo.Initial(Guid.Empty, DateTime.UtcNow,
			VNodeState.Unknown, true,
			new DnsEndPoint(ipAddress, port),
			null, 0, 0, false);

		var ipEndPoint = new IPEndPoint(IPAddress.Parse(ipAddress), port);
		var dnsEndPoint = new DnsEndPoint(ipAddress, port);

		Assert.True(memberWithDnsEndPoint.Is(ipEndPoint));
		Assert.True(memberWithDnsEndPoint.Is(dnsEndPoint));
	}

	[Test]
	public void member_with_ip_endpoint_should_equal()
	{
		var ipAddress = "127.0.0.1";
		var port = 1113;
		var memberWithDnsEndPoint = EventStore.Core.Cluster.MemberInfo.Initial(Guid.Empty, DateTime.UtcNow,
			VNodeState.Unknown, true,
			new IPEndPoint(IPAddress.Parse(ipAddress), port),
			null, 0, 0, false);

		var ipEndPoint = new IPEndPoint(IPAddress.Parse(ipAddress), port);
		var dnsEndPoint = new DnsEndPoint(ipAddress, port);

		Assert.True(memberWithDnsEndPoint.Is(ipEndPoint));
		Assert.True(memberWithDnsEndPoint.Is(dnsEndPoint));
	}

	[Test]
	public void internal_gossip_round_trip_preserves_the_grpc_replication_endpoint()
	{
		var replicationEndPoint = new DnsEndPoint("replication-node", 1112);
		var httpEndPoint = new DnsEndPoint("public-node", 2113);
		var member = EventStore.Core.Cluster.MemberInfo.Initial(
			Guid.NewGuid(),
			DateTime.UtcNow,
			VNodeState.Unknown,
			true,
			httpEndPoint,
			null,
			0,
			0,
			false,
			replicationEndPoint: replicationEndPoint);

		var grpc = ToGrpcClusterInfo(new EventStore.Core.Cluster.ClusterInfo(member));
		var roundTrip = FromGrpcClusterInfo(grpc);

		Assert.That(roundTrip.Members, Has.Length.EqualTo(1));
		Assert.That(roundTrip.Members[0].HttpEndPoint, Is.EqualTo(httpEndPoint));
		Assert.That(roundTrip.Members[0].ReplicationEndPoint, Is.EqualTo(replicationEndPoint));
	}

	[Test]
	public void member_without_a_replication_endpoint_uses_its_http_endpoint()
	{
		var httpEndPoint = new DnsEndPoint("mixed-version-node", 2113);
		var member = EventStore.Core.Cluster.MemberInfo.Initial(
			Guid.NewGuid(),
			DateTime.UtcNow,
			VNodeState.Unknown,
			true,
			httpEndPoint,
			null,
			0,
			0,
			false);
		var grpc = ToGrpcClusterInfo(new EventStore.Core.Cluster.ClusterInfo(member));
		grpc.Members[0].ReplicationEndPoint = null;

		var roundTrip = FromGrpcClusterInfo(grpc);

		Assert.That(roundTrip.Members[0].ReplicationEndPoint, Is.EqualTo(httpEndPoint));
	}

	private static EventStore.Cluster.ClusterInfo ToGrpcClusterInfo(EventStore.Core.Cluster.ClusterInfo clusterInfo) =>
		(EventStore.Cluster.ClusterInfo)typeof(EventStore.Core.Cluster.ClusterInfo)
			.GetMethod("ToGrpcClusterInfo", BindingFlags.Static | BindingFlags.NonPublic)
			.Invoke(null, [clusterInfo]);

	private static EventStore.Core.Cluster.ClusterInfo FromGrpcClusterInfo(EventStore.Cluster.ClusterInfo clusterInfo) =>
		(EventStore.Core.Cluster.ClusterInfo)typeof(EventStore.Core.Cluster.ClusterInfo)
			.GetMethod("FromGrpcClusterInfo", BindingFlags.Static | BindingFlags.NonPublic)
			.Invoke(null, [clusterInfo, null]);
}
