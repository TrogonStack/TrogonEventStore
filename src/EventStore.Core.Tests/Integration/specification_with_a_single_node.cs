using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using EventStore.ClientAPI.SystemData;
using EventStore.Core.Bus;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Integration;

public abstract class specification_with_a_single_node<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	protected MiniNode<TLogFormat, TStreamId> _node;

	protected virtual TimeSpan Timeout { get; } = TimeSpan.FromSeconds(3);

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_node = new MiniNode<TLogFormat, TStreamId>(PathName, dbPath: Path.Combine(PathName, "db"), inMemDb: false);

		BeforeNodeStarts();

		await _node.Start();

		try
		{
			await Given().WithTimeout(Timeout);
		}
		catch (Exception ex)
		{
			throw new Exception("Given Failed", ex);
		}
	}

	protected virtual void BeforeNodeStarts()
	{
	}

	protected virtual Task Given() => Task.CompletedTask;

	protected async Task ShutdownNode()
	{
		await _node.Shutdown(keepDb: true);
		_node = null;
	}

	protected Task StartNode()
	{
		if (_node == null)
			_node = new MiniNode<TLogFormat, TStreamId>(PathName, dbPath: Path.Combine(PathName, "db"), inMemDb: false);

		BeforeNodeStarts();

		return _node.Start();
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		await _node.Shutdown();
		_node = null;
		await base.TestFixtureTearDown();
	}
}
