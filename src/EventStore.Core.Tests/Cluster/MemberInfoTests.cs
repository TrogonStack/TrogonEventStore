using System;
using System.Net;
using System.Reflection;
using EventStore.Core.Data;
using NUnit.Framework;

namespace EventStore.Core.Tests.Cluster;

[TestFixture]
public class MemberInfoTests
{
	private static readonly DnsEndPoint InternalTcp = new("internal", 1112);
	private static readonly DnsEndPoint InternalSecureTcp = new("internal-secure", 2112);
	private static readonly DnsEndPoint ExternalTcp = new("external", 1113);
	private static readonly DnsEndPoint ExternalSecureTcp = new("external-secure", 2113);
	private static readonly DnsEndPoint Http = new("http", 2113);
	private static readonly DnsEndPoint Replication = new("replication", 3113);

	[Test]
	public void member_with_dns_endpoint_should_equal()
	{
		var ipAddress = "127.0.0.1";
		var port = 1113;
		var memberWithDnsEndPoint = EventStore.Core.Cluster.MemberInfo.Initial(Guid.Empty, DateTime.UtcNow,
			VNodeState.Unknown, true,
			new DnsEndPoint(ipAddress, port),
			new DnsEndPoint(ipAddress, port),
			new DnsEndPoint(ipAddress, port),
			new DnsEndPoint(ipAddress, port),
			new DnsEndPoint(ipAddress, port),
			null, 0, 0,
			0, false);

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
			new IPEndPoint(IPAddress.Parse(ipAddress), port),
			new IPEndPoint(IPAddress.Parse(ipAddress), port),
			new IPEndPoint(IPAddress.Parse(ipAddress), port),
			new IPEndPoint(IPAddress.Parse(ipAddress), port),
			null, 0, 0, 0, false);

		var ipEndPoint = new IPEndPoint(IPAddress.Parse(ipAddress), port);
		var dnsEndPoint = new DnsEndPoint(ipAddress, port);

		Assert.True(memberWithDnsEndPoint.Is(ipEndPoint));
		Assert.True(memberWithDnsEndPoint.Is(dnsEndPoint));
	}

	[Test]
	public void grpc_round_trip_preserves_tcp_and_cluster_endpoints()
	{
		var member = EventStore.Core.Cluster.MemberInfo.Initial(Guid.NewGuid(), DateTime.UtcNow,
			VNodeState.Unknown, true,
			InternalTcp, null, null, ExternalSecureTcp, Http,
			"client", 2113, 1113, 0, false, clusterEndPoint: Replication);

		var result = FromGrpcClusterInfo(ToGrpcClusterInfo(
			new EventStore.Core.Cluster.ClusterInfo(member))).Members[0];

		Assert.That(result.InternalTcpEndPoint, Is.EqualTo(InternalTcp));
		Assert.That(result.InternalSecureTcpEndPoint, Is.Null);
		Assert.That(result.ExternalTcpEndPoint, Is.Null);
		Assert.That(result.ExternalSecureTcpEndPoint, Is.EqualTo(ExternalSecureTcp));
		Assert.That(result.HttpEndPoint, Is.EqualTo(Http));
		Assert.That(result.ClusterEndPoint, Is.EqualTo(Replication));
	}

	[Test]
	public void explicit_cluster_endpoint_is_recognized_without_replacing_tcp_endpoints()
	{
		var member = CreateMember(Replication);
		var vnode = new VNodeInfo(Guid.NewGuid(), 0,
			new IPEndPoint(IPAddress.Loopback, 1112), null,
			new IPEndPoint(IPAddress.Loopback, 1113), null,
			Http, false, Replication);
		var advertise = new GossipAdvertiseInfo(
			InternalTcp, InternalSecureTcp, ExternalTcp, ExternalSecureTcp, Http,
			null, null, 0, null, 0, 0, Replication);

		Assert.That(member.Is(Replication), Is.True);
		Assert.That(member.InternalTcpEndPoint, Is.EqualTo(InternalTcp));
		Assert.That(member.InternalSecureTcpEndPoint, Is.EqualTo(InternalSecureTcp));
		Assert.That(member.ExternalTcpEndPoint, Is.EqualTo(ExternalTcp));
		Assert.That(member.ExternalSecureTcpEndPoint, Is.EqualTo(ExternalSecureTcp));
		Assert.That(vnode.ClusterEndPoint, Is.SameAs(Replication));
		Assert.That(advertise.ClusterEndPoint, Is.SameAs(Replication));
	}

	[Test]
	public void client_member_preserves_the_cluster_endpoint()
	{
		var clientMember = new EventStore.Core.Cluster.ClientClusterInfo.ClientMemberInfo(
			CreateMember(Replication));

		Assert.That(clientMember.ClusterEndPointIp, Is.EqualTo(Replication.Host));
		Assert.That(clientMember.ClusterEndPointPort, Is.EqualTo(Replication.Port));
	}

	[Test]
	public void client_cluster_info_excludes_internal_discovery_placeholders()
	{
		var member = CreateMember(Replication);
		var seed = EventStore.Core.Cluster.MemberInfo.ForManager(
			Guid.Empty,
			DateTime.UtcNow,
			true,
			Replication,
			clusterEndPoint: Replication);

		var clientCluster = new EventStore.Core.Cluster.ClientClusterInfo(
			new EventStore.Core.Cluster.ClusterInfo(member, seed),
			Http.Host,
			Http.Port);

		Assert.That(clientCluster.Members, Has.Length.EqualTo(1));
		Assert.That(clientCluster.Members[0].InstanceId, Is.EqualTo(member.InstanceId));
	}

	[Test]
	public void missing_cluster_endpoint_falls_back_to_http_endpoint()
	{
		var member = CreateMember();
		var vnode = new VNodeInfo(Guid.NewGuid(), 0,
			new IPEndPoint(IPAddress.Loopback, 1112), null,
			new IPEndPoint(IPAddress.Loopback, 1113), null,
			Http, false);
		var advertise = new GossipAdvertiseInfo(
			InternalTcp, InternalSecureTcp, ExternalTcp, ExternalSecureTcp, Http,
			null, null, 0, null, 0, 0);

		Assert.That(member.ClusterEndPoint, Is.SameAs(Http));
		Assert.That(vnode.ClusterEndPoint, Is.SameAs(Http));
		Assert.That(advertise.ClusterEndPoint, Is.SameAs(Http));
	}

	[Test]
	public void grpc_member_without_cluster_endpoint_falls_back_to_http_endpoint()
	{
		var grpcCluster = ToGrpcClusterInfo(
			new EventStore.Core.Cluster.ClusterInfo(CreateMember(Replication)));
		grpcCluster.Members[0].ReplicationEndPoint = null;

		var result = FromGrpcClusterInfo(grpcCluster).Members[0];

		Assert.That(result.ClusterEndPoint, Is.EqualTo(Http));
	}

	private static EventStore.Core.Cluster.MemberInfo CreateMember(DnsEndPoint clusterEndPoint = null) =>
		EventStore.Core.Cluster.MemberInfo.Initial(Guid.NewGuid(), DateTime.UtcNow,
			VNodeState.Unknown, true,
			InternalTcp, InternalSecureTcp, ExternalTcp, ExternalSecureTcp, Http,
			"client", 2113, 1113, 0, false, clusterEndPoint: clusterEndPoint);

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
