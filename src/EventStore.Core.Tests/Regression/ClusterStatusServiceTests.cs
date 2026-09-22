using System.Collections.Generic;
using System.Reflection;
using EventStore.ClusterNode.Components.Services;
using EventStore.Core.Cluster;
using NUnit.Framework;

namespace EventStore.Core.Tests.Regression;

[TestFixture]
public class ClusterStatusServiceTests
{
	[Test]
	public void replication_statistics_match_the_member_cluster_endpoint()
	{
		var member = new ClientClusterInfo.ClientMemberInfo
		{
			ClusterEndPointIp = "replica.internal",
			ClusterEndPointPort = 1112
		};

		var result = (ClientClusterInfo.ClientMemberInfo)typeof(ClusterStatusService)
			.GetMethod("FindMemberByInternalEndpoint", BindingFlags.NonPublic | BindingFlags.Static)!
			.Invoke(null, [new List<ClientClusterInfo.ClientMemberInfo> { member }, "replica.internal:1112"])!;

		Assert.That(result, Is.SameAs(member));
	}
}
