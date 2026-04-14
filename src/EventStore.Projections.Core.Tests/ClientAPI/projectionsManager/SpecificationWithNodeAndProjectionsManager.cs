using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Common.Log;
using EventStore.ClientAPI.Projections;
using EventStore.ClientAPI.SystemData;
using EventStore.Common.Options;
using EventStore.Core.Services;
using EventStore.Core.Tests;
using EventStore.Core.Tests.ClientAPI.Helpers;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Util;
using EventStore.Projections.Core.Services.Processing;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.ClientAPI.projectionsManager;

public abstract class SpecificationWithNodeAndProjectionsManager<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	protected MiniNode<TLogFormat, TStreamId> _node;
	protected ProjectionsManager _projManager;
	protected IEventStoreConnection _connection;
	protected UserCredentials _credentials;
	protected TimeSpan _timeout;
	protected string _tag;
	private Task _systemProjectionsCreated;
	private ProjectionsSubsystem _projectionsSubsystem;


	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_credentials = new UserCredentials(SystemUsers.Admin, SystemUsers.DefaultAdminPassword);
		_timeout = TimeSpan.FromSeconds(20);
		// Check if a node is running in ProjectionsManagerTestSuiteMarkerBase
		_tag = "_1";

		_node = CreateNode();
		await _node.Start();

		await _systemProjectionsCreated.WithTimeout(_timeout);

		await ReconnectUntilReady();

		_projManager = new ProjectionsManager(new ConsoleLogger(), _node.HttpEndPoint, _timeout, _node.HttpMessageHandler);
		try
		{
			await Given().WithTimeout(_timeout);
		}
		catch (Exception ex)
		{
			throw new Exception("Given Failed", ex);
		}

		try
		{
			await When().WithTimeout(_timeout);
		}
		catch (Exception ex)
		{
			throw new Exception("When Failed", ex);
		}
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		if (_connection != null)
			await CloseConnectionAndWait(_connection);

		DisposeIfNeeded(_projManager);

		await _node.Shutdown();
		await Task.Delay(1000);

		await base.TestFixtureTearDown();
	}

	public abstract Task Given();
	public abstract Task When();

	protected MiniNode<TLogFormat, TStreamId> CreateNode()
	{
		_projectionsSubsystem = new ProjectionsSubsystem(new ProjectionSubsystemOptions(1, ProjectionType.All, false, TimeSpan.FromMinutes(Opts.ProjectionsQueryExpiryDefault), Opts.FaultOutOfOrderProjectionsDefault, 500, 250));
		_systemProjectionsCreated = SystemProjections.Created(_projectionsSubsystem.LeaderInputBus);
		return new MiniNode<TLogFormat, TStreamId>(
			PathName, inMemDb: true,
			subsystems: [_projectionsSubsystem]);
	}

	protected EventData CreateEvent(string eventType, string data)
	{
		return new EventData(Guid.NewGuid(), eventType, true, Encoding.UTF8.GetBytes(data), null);
	}

	protected Task PostEvent(string stream, string eventType, string data)
	{
		return _connection.AppendToStreamAsync(stream, ExpectedVersion.Any, new[] { CreateEvent(eventType, data) });
	}

	private async Task CloseConnectionAndWait(IEventStoreConnection connection)
	{
		var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.Closed += (_, _) => closed.TrySetResult();
		connection.Close();
		await closed.Task.WithTimeout(_timeout);
	}

	private async Task ReconnectUntilReady()
	{
		var deadline = DateTime.UtcNow + _timeout;

		while (true)
		{
			try
			{
				_connection = TestConnection.CreateMiniNodeClient(_node.TcpEndPoint);
				await _connection.ConnectAsync();
				await _connection.ReadAllEventsForwardAsync(Position.Start, 1, false, _credentials);
				return;
			}
			catch (Exception ex) when (IsTransientConnectionFailure(ex) && DateTime.UtcNow < deadline)
			{
				if (_connection != null)
					TryCloseConnection(_connection);
				await Task.Delay(250);
			}
		}
	}

	private static bool IsTransientConnectionFailure(Exception ex) =>
		ex.GetType().Name is "ConnectionClosedException"
			or "RetriesLimitReachedException"
			or "NotAuthenticatedException"
			or "AccessDeniedException";

	private static void TryCloseConnection(IEventStoreConnection connection)
	{
		try
		{
			connection.Close();
		}
		catch
		{
		}
	}

	private static void DisposeIfNeeded(object candidate)
	{
		if (candidate is IDisposable disposable)
			disposable.Dispose();
	}

	protected Task CreateOneTimeProjection()
	{
		var query = CreateStandardQuery(Guid.NewGuid().ToString());
		return _projManager.CreateOneTimeAsync(query, _credentials);
	}

	protected Task CreateContinuousProjection(string projectionName)
	{
		var query = CreateStandardQuery(Guid.NewGuid().ToString());
		return _projManager.CreateContinuousAsync(projectionName, query, _credentials);
	}

	protected string CreateStandardQuery(string stream)
	{
		return @"fromStream(""" + stream + @""")
                .when({
                    ""$any"":function(s,e) {
                        s.count = 1;
                        return s;
                    }
            });";
	}

	protected string CreateEmittingQuery(string stream, string emittingStream)
	{
		return @"fromStream(""" + stream + @""")
                .when({
                    ""$any"":function(s,e) {
                        emit(""" + emittingStream + @""", ""emittedEvent"", e);
                    }
                });";
	}

	private List<string> _systemProjections =>
		typeof(ProjectionNamesBuilder.StandardProjections).GetFields(
				System.Reflection.BindingFlags.Public |
				System.Reflection.BindingFlags.Static |
				System.Reflection.BindingFlags.FlattenHierarchy)
			.Where(x => x.IsLiteral && !x.IsInitOnly)
			.Select(x => x.GetRawConstantValue().ToString())
			.ToList();
}
