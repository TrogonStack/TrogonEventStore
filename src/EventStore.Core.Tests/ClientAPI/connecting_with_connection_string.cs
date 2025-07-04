using System;
using System.Linq;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.ClientAPI;

[Category("ClientAPI"), Category("LongRunning")]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class when_connecting_with_connection_string<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	private MiniNode<TLogFormat, TStreamId> _node;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_node = new MiniNode<TLogFormat, TStreamId>(PathName);
		await _node.Start();
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		await _node.Shutdown();
		await base.TestFixtureTearDown();
	}

	[Test]
	public async Task should_not_throw_when_connect_to_is_set()
	{
		string connectionString = string.Format("ConnectTo=tcp://{0};", _node.TcpEndPoint);
		using (var connection = EventStoreConnection.Create(connectionString))
		{
			await connection.ConnectAsync();
			connection.Close();
		}
	}

	[Test]
	public void should_not_throw_when_only_gossip_seeds_is_set()
	{
		string connectionString = string.Format("GossipSeeds={0};", _node.HttpEndPoint);
		IEventStoreConnection connection = null;

		Assert.DoesNotThrow(() => connection = EventStoreConnection.Create(connectionString));
		Assert.AreEqual(_node.HttpEndPoint, connection.Settings.GossipSeeds.First().EndPoint);

		connection.Dispose();
	}

	[Test]
	public void should_throw_when_gossip_seeds_and_connect_to_is_set()
	{
		string connectionString = string.Format("ConnectTo=tcp://{0};GossipSeeds={1}", _node.TcpEndPoint,
			_node.HttpEndPoint);
		Assert.Throws<NotSupportedException>(() => EventStoreConnection.Create(connectionString));
	}

	[Test]
	public void should_throw_when_neither_gossip_seeds_nor_connect_to_is_set()
	{
		string connectionString = string.Format("HeartBeatTimeout=2000");
		Assert.Throws<Exception>(() => EventStoreConnection.Create(connectionString));
	}
}
