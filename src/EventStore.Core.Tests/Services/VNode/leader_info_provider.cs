using System;
using System.Collections.Generic;
using System.Net;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Services.VNode;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.VNode;

[TestFixture]
public class leader_info_provider
{
	public static IEnumerable<TestCaseData> Cases()
	{
		yield return Case("leader endpoint", Leader("1.1.1.1", 2113), Gossip("2.2.2.2", 2113), "1.1.1.1", 2113);
		yield return Case("leader advertised host", Leader("1.1.1.1", 2113, "leader.example"),
			Gossip("2.2.2.2", 2113), "leader.example", 2113);
		yield return Case("leader advertised port", Leader("1.1.1.1", 2113, advertisePort: 3113),
			Gossip("2.2.2.2", 2113), "1.1.1.1", 3113);
		yield return Case("leader advertised endpoint", Leader("1.1.1.1", 2113, "leader.example", 3113),
			Gossip("2.2.2.2", 2113), "leader.example", 3113);
		yield return Case("local gossip endpoint", null, Gossip("2.2.2.2", 2113), "2.2.2.2", 2113);
		yield return Case("local advertised endpoint", null, Gossip("2.2.2.2", 2113, "node.example", 3113),
			"node.example", 3113);
	}

	[TestCaseSource(nameof(Cases))]
	public void returns_the_advertised_http_endpoint(MemberInfo leader, GossipAdvertiseInfo gossip, EndPoint expected)
	{
		var result = new LeaderInfoProvider(gossip, leader).GetLeaderInfoEndPoint();

		Assert.AreEqual(expected, result);
	}

	private static TestCaseData Case(string name, MemberInfo leader, GossipAdvertiseInfo gossip,
		string expectedHost, int expectedPort) =>
		new TestCaseData(leader, gossip, new DnsEndPoint(expectedHost, expectedPort)).SetName(name);

	private static MemberInfo Leader(string host, int port, string advertiseHost = null, int advertisePort = 0) =>
		MemberInfo.Initial(
			Guid.NewGuid(),
			DateTime.UtcNow,
			VNodeState.Leader,
			true,
			new DnsEndPoint(host, port),
			advertiseHost,
			advertisePort,
			0,
			false);

	private static GossipAdvertiseInfo Gossip(string host, int port, string advertiseHost = null,
		int advertisePort = 0) =>
		new(new DnsEndPoint(host, port), advertiseHost, advertisePort);
}
