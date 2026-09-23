using System.Net;
using EventStore.ClusterNode.Components.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Regression;

[TestFixture]
public class EndpointPolicyTests
{
	[TestCase(2113, "/streams", true)]
	[TestCase(2113, "/event_store.client.gossip.Gossip/Read", true)]
	[TestCase(2113, "/event_store.cluster.Gossip/Read", false)]
	[TestCase(2113, "/event_store.cluster.Elections/Prepare", false)]
	[TestCase(2113, "/event_store.replication.Replication/Replicate", false)]
	[TestCase(2113, "/event_store.forwarding.RequestForwarding/Forward", false)]
	[TestCase(1112, "/streams", false)]
	[TestCase(1112, "/event_store.client.gossip.Gossip/Read", false)]
	[TestCase(1112, "/event_store.cluster.Gossip/Read", true)]
	[TestCase(1112, "/event_store.cluster.Elections/Prepare", true)]
	[TestCase(1112, "/event_store.replication.Replication/Replicate", true)]
	[TestCase(1112, "/event_store.forwarding.RequestForwarding/Forward", true)]
	public void routes_are_isolated_by_endpoint_role(int localPort, string path, bool expected)
	{
		var policy = CreatePolicy(
			new IPEndPoint(IPAddress.Loopback, 2113),
			new IPEndPoint(IPAddress.Loopback, 1112));
		var context = new DefaultHttpContext();
		context.Connection.LocalIpAddress = IPAddress.Loopback;
		context.Connection.LocalPort = localPort;
		context.Request.Path = path;

		Assert.That(policy.Allows(context), Is.EqualTo(expected));
	}

	[Test]
	public void wildcard_cluster_binding_matches_the_resolved_local_address()
	{
		var policy = CreatePolicy(
			new IPEndPoint(IPAddress.Loopback, 2113),
			new IPEndPoint(IPAddress.Any, 1112));
		var context = new DefaultHttpContext();
		context.Connection.LocalIpAddress = IPAddress.Parse("192.0.2.1");
		context.Connection.LocalPort = 1112;
		context.Request.Path = "/event_store.cluster.Gossip/Read";

		Assert.That(policy.Allows(context), Is.True);
	}

	[TestCase("/streams", true)]
	[TestCase("/event_store.cluster.Gossip/Read", false)]
	public void listeners_without_an_ip_endpoint_remain_client_only(string path, bool expected)
	{
		var policy = CreatePolicy(
			new IPEndPoint(IPAddress.Loopback, 2113),
			new IPEndPoint(IPAddress.Loopback, 1112));
		var context = new DefaultHttpContext();
		context.Connection.LocalIpAddress = null;
		context.Connection.LocalPort = 0;
		context.Request.Path = path;

		Assert.That(policy.Allows(context), Is.EqualTo(expected));
	}

	[Test]
	public void additional_routes_can_be_assigned_without_changing_the_policy()
	{
		var policy = new EndpointPolicy(
			[
				new(EndpointRole.Client, new IPEndPoint(IPAddress.Loopback, 2113), HttpProtocols.Http1AndHttp2),
				new(EndpointRole.Cluster, new IPEndPoint(IPAddress.Loopback, 1112), HttpProtocols.Http2),
			],
			[new(EventStore.Client.Monitoring.Monitoring.Descriptor, EndpointRole.Cluster)],
			defaultRouteRole: EndpointRole.Client,
			nonIpEndpointRole: EndpointRole.Client);
		var context = new DefaultHttpContext();
		context.Connection.LocalIpAddress = IPAddress.Loopback;
		context.Connection.LocalPort = 1112;
		context.Request.Path = "/event_store.client.monitoring.Monitoring/Stats";

		Assert.That(policy.Allows(context), Is.True);
	}

	[Test]
	public void unregistered_ip_listeners_are_denied()
	{
		var policy = CreatePolicy(
			new IPEndPoint(IPAddress.Loopback, 2113),
			new IPEndPoint(IPAddress.Loopback, 1112));
		var context = new DefaultHttpContext();
		context.Connection.LocalIpAddress = IPAddress.Loopback;
		context.Connection.LocalPort = 3112;
		context.Request.Path = "/streams";

		Assert.That(policy.Allows(context), Is.False);
	}

	[Test]
	public void bindings_carry_their_transport_protocols()
	{
		var binding = new EndpointBinding(
			EndpointRole.Cluster,
			new IPEndPoint(IPAddress.Loopback, 1112),
			HttpProtocols.Http2);

		Assert.That(binding.Protocols, Is.EqualTo(HttpProtocols.Http2));
	}

	private static EndpointPolicy CreatePolicy(IPEndPoint clientEndPoint, IPEndPoint clusterEndPoint) =>
		new(
			[
				new(EndpointRole.Client, clientEndPoint, HttpProtocols.Http1AndHttp2),
				new(EndpointRole.Cluster, clusterEndPoint, HttpProtocols.Http2),
			],
			[
				new(EventStore.Cluster.Gossip.Descriptor, EndpointRole.Cluster),
				new(EventStore.Cluster.Elections.Descriptor, EndpointRole.Cluster),
				new(EventStore.Replication.Replication.Descriptor, EndpointRole.Cluster),
				new(EventStore.Forwarding.RequestForwarding.Descriptor, EndpointRole.Cluster),
			],
			defaultRouteRole: EndpointRole.Client,
			nonIpEndpointRole: EndpointRole.Client);
}
