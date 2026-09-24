using System;
using System.Net;
using System.Reflection;
using EventStore.Core.Data;
using NUnit.Framework;

namespace EventStore.Core.Tests.Cluster;

[TestFixture]
public class MemberInfoTests
{
	private static readonly DnsEndPoint Http = new("http", 2113);
	private static readonly DnsEndPoint Cluster = new("cluster", 1112);

	[Test]
	public void member_with_dns_endpoint_should_equal()
	{
		var ipAddress = "127.0.0.1";
		var port = 1113;
		var member = EventStore.Core.Cluster.MemberInfo.Initial(Guid.Empty, DateTime.UtcNow,
			VNodeState.Unknown, true, new DnsEndPoint(ipAddress, port), null, 0, 0, false);

		Assert.That(member.Is(new IPEndPoint(IPAddress.Parse(ipAddress), port)), Is.True);
		Assert.That(member.Is(new DnsEndPoint(ipAddress, port)), Is.True);
	}

	[Test]
	public void member_with_ip_endpoint_should_equal()
	{
		var ipAddress = "127.0.0.1";
		var port = 1113;
		var member = EventStore.Core.Cluster.MemberInfo.Initial(Guid.Empty, DateTime.UtcNow,
			VNodeState.Unknown, true, new IPEndPoint(IPAddress.Parse(ipAddress), port), null, 0, 0, false);

		Assert.That(member.Is(new IPEndPoint(IPAddress.Parse(ipAddress), port)), Is.True);
		Assert.That(member.Is(new DnsEndPoint(ipAddress, port)), Is.True);
	}

	[Test]
	public void grpc_round_trip_preserves_client_and_cluster_endpoints()
	{
		var result = FromGrpcClusterInfo(ToGrpcClusterInfo(
			new EventStore.Core.Cluster.ClusterInfo(CreateMember(Cluster)))).Members[0];

		Assert.That(result.HttpEndPoint, Is.EqualTo(Http));
		Assert.That(result.ClusterEndPoint, Is.EqualTo(Cluster));
		Assert.That(result.ReplicationEndPoint, Is.EqualTo(Cluster));
	}

	[Test]
	public void explicit_cluster_endpoint_is_recognized()
	{
		var member = CreateMember(Cluster);
		var vnode = new VNodeInfo(Guid.NewGuid(), 0, Http, false, Cluster);
		var advertise = new GossipAdvertiseInfo(Http, null, 0, Cluster);

		Assert.That(member.Is(Cluster), Is.True);
		Assert.That(vnode.ClusterEndPoint, Is.SameAs(Cluster));
		Assert.That(advertise.ClusterEndPoint, Is.SameAs(Cluster));
	}

	[Test]
	public void client_member_preserves_the_cluster_endpoint()
	{
		var clientMember = new EventStore.Core.Cluster.ClientClusterInfo.ClientMemberInfo(CreateMember(Cluster));

		Assert.That(clientMember.ClusterEndPointIp, Is.EqualTo(Cluster.Host));
		Assert.That(clientMember.ClusterEndPointPort, Is.EqualTo(Cluster.Port));
	}

	[Test]
	public void client_cluster_info_excludes_internal_discovery_placeholders()
	{
		var member = CreateMember(Cluster);
		var seed = EventStore.Core.Cluster.MemberInfo.ForManager(
			Guid.Empty, DateTime.UtcNow, true, Cluster, clusterEndPoint: Cluster);

		var clientCluster = new EventStore.Core.Cluster.ClientClusterInfo(
			new EventStore.Core.Cluster.ClusterInfo(member, seed), Http.Host, Http.Port);

		Assert.That(clientCluster.Members, Has.Length.EqualTo(1));
		Assert.That(clientCluster.Members[0].InstanceId, Is.EqualTo(member.InstanceId));
	}

	[Test]
	public void missing_cluster_endpoint_falls_back_to_http_endpoint()
	{
		var member = CreateMember();
		var vnode = new VNodeInfo(Guid.NewGuid(), 0, Http, false);
		var advertise = new GossipAdvertiseInfo(Http, null, 0);

		Assert.That(member.ClusterEndPoint, Is.SameAs(Http));
		Assert.That(vnode.ClusterEndPoint, Is.SameAs(Http));
		Assert.That(advertise.ClusterEndPoint, Is.SameAs(Http));
	}

	[Test]
	public void grpc_member_without_cluster_endpoint_falls_back_to_http_endpoint()
	{
		var grpcCluster = ToGrpcClusterInfo(
			new EventStore.Core.Cluster.ClusterInfo(CreateMember(Cluster)));
		grpcCluster.Members[0].ReplicationEndPoint = null;

		var result = FromGrpcClusterInfo(grpcCluster).Members[0];

		Assert.That(result.ClusterEndPoint, Is.EqualTo(Http));
	}

	private static EventStore.Core.Cluster.MemberInfo CreateMember(DnsEndPoint clusterEndPoint = null) =>
		EventStore.Core.Cluster.MemberInfo.Initial(Guid.NewGuid(), DateTime.UtcNow,
			VNodeState.Unknown, true, Http, null, 0, 0, false, clusterEndPoint: clusterEndPoint);

	private static EventStore.Cluster.ClusterInfo ToGrpcClusterInfo(
		EventStore.Core.Cluster.ClusterInfo clusterInfo) =>
		(EventStore.Cluster.ClusterInfo)typeof(EventStore.Core.Cluster.ClusterInfo)
			.GetMethod("ToGrpcClusterInfo", BindingFlags.NonPublic | BindingFlags.Static)!
			.Invoke(null, [clusterInfo])!;

	private static EventStore.Core.Cluster.ClusterInfo FromGrpcClusterInfo(
		EventStore.Cluster.ClusterInfo clusterInfo) =>
		(EventStore.Core.Cluster.ClusterInfo)typeof(EventStore.Core.Cluster.ClusterInfo)
			.GetMethod("FromGrpcClusterInfo", BindingFlags.NonPublic | BindingFlags.Static)!
			.Invoke(null, [clusterInfo, null])!;
}
