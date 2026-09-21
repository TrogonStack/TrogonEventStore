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
	public void grpc_round_trip_preserves_tcp_and_replication_endpoints()
	{
		var member = EventStore.Core.Cluster.MemberInfo.Initial(Guid.NewGuid(), DateTime.UtcNow,
			VNodeState.Unknown, true,
			InternalTcp, null, null, ExternalSecureTcp, Http,
			"client", 2113, 1113, 0, false, replicationEndPoint: Replication);

		var result = FromGrpcClusterInfo(ToGrpcClusterInfo(
			new EventStore.Core.Cluster.ClusterInfo(member))).Members[0];

		Assert.That(result.InternalTcpEndPoint, Is.EqualTo(InternalTcp));
		Assert.That(result.InternalSecureTcpEndPoint, Is.Null);
		Assert.That(result.ExternalTcpEndPoint, Is.Null);
		Assert.That(result.ExternalSecureTcpEndPoint, Is.EqualTo(ExternalSecureTcp));
		Assert.That(result.HttpEndPoint, Is.EqualTo(Http));
		Assert.That(result.ReplicationEndPoint, Is.EqualTo(Replication));
	}

	[Test]
	public void explicit_replication_endpoint_is_recognized_without_replacing_tcp_endpoints()
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
		Assert.That(vnode.ReplicationEndPoint, Is.SameAs(Replication));
		Assert.That(advertise.ReplicationEndPoint, Is.SameAs(Replication));
	}

	[Test]
	public void missing_replication_endpoint_falls_back_to_http_endpoint()
	{
		var member = CreateMember();
		var vnode = new VNodeInfo(Guid.NewGuid(), 0,
			new IPEndPoint(IPAddress.Loopback, 1112), null,
			new IPEndPoint(IPAddress.Loopback, 1113), null,
			Http, false);
		var advertise = new GossipAdvertiseInfo(
			InternalTcp, InternalSecureTcp, ExternalTcp, ExternalSecureTcp, Http,
			null, null, 0, null, 0, 0);

		Assert.That(member.ReplicationEndPoint, Is.SameAs(Http));
		Assert.That(vnode.ReplicationEndPoint, Is.SameAs(Http));
		Assert.That(advertise.ReplicationEndPoint, Is.SameAs(Http));
	}

	[Test]
	public void grpc_member_without_replication_endpoint_falls_back_to_http_endpoint()
	{
		var grpcCluster = ToGrpcClusterInfo(
			new EventStore.Core.Cluster.ClusterInfo(CreateMember(Replication)));
		grpcCluster.Members[0].ReplicationEndPoint = null;

		var result = FromGrpcClusterInfo(grpcCluster).Members[0];

		Assert.That(result.ReplicationEndPoint, Is.EqualTo(Http));
	}

	private static EventStore.Core.Cluster.MemberInfo CreateMember(DnsEndPoint replicationEndPoint = null) =>
		EventStore.Core.Cluster.MemberInfo.Initial(Guid.NewGuid(), DateTime.UtcNow,
			VNodeState.Unknown, true,
			InternalTcp, InternalSecureTcp, ExternalTcp, ExternalSecureTcp, Http,
			"client", 2113, 1113, 0, false, replicationEndPoint: replicationEndPoint);

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
