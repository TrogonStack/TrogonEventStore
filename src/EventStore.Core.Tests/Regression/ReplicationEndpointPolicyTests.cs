using System.Net;
using EventStore.ClusterNode.Components.Services;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EventStore.Core.Tests.Regression;

[TestFixture]
public class ReplicationEndpointPolicyTests
{
	[TestCase(2113, "/streams", true)]
	[TestCase(2113, "/event_store.replication.Replication/Replicate", false)]
	[TestCase(2113, "/event_store.forwarding.RequestForwarding/Forward", false)]
	[TestCase(1112, "/streams", false)]
	[TestCase(1112, "/event_store.replication.Replication/Replicate", true)]
	[TestCase(1112, "/event_store.forwarding.RequestForwarding/Forward", true)]
	public void internal_services_are_isolated_from_the_public_node_listener(
		int localPort,
		string path,
		bool expected)
	{
		var policy = new ReplicationEndpointPolicy(new IPEndPoint(IPAddress.Loopback, 1112));
		var context = new DefaultHttpContext();
		context.Connection.LocalIpAddress = IPAddress.Loopback;
		context.Connection.LocalPort = localPort;
		context.Request.Path = path;

		Assert.That(policy.Allows(context), Is.EqualTo(expected));
	}

	[Test]
	public void wildcard_replication_binding_matches_the_resolved_local_address()
	{
		var policy = new ReplicationEndpointPolicy(new IPEndPoint(IPAddress.Any, 1112));
		var context = new DefaultHttpContext();
		context.Connection.LocalIpAddress = IPAddress.Parse("192.0.2.1");
		context.Connection.LocalPort = 1112;
		context.Request.Path = "/event_store.replication.Replication/Replicate";

		Assert.That(policy.Allows(context), Is.True);
	}
}
