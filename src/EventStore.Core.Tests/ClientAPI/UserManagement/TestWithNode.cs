using System;
using System.Text;
using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Common.Log;
using EventStore.ClientAPI.SystemData;
using EventStore.ClientAPI.UserManagement;
using EventStore.Core.Tests.ClientAPI.Helpers;
using EventStore.Core.Tests.Helpers;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;
using UsersClient = EventStore.Client.Users.Users.UsersClient;

namespace EventStore.Core.Tests.ClientAPI.UserManagement;

[Category("LongRunning"), Category("ClientAPI")]
public abstract class TestWithNode<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	protected MiniNode<TLogFormat, TStreamId> _node;
	protected UsersManager _manager;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_node = new MiniNode<TLogFormat, TStreamId>(PathName);
		await _node.Start();
		_manager = new UsersManager(new NoopLogger(), _node.HttpEndPoint, TimeSpan.FromSeconds(5), httpMessageHandler: _node.HttpMessageHandler);
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		await _node.Shutdown();
		await base.TestFixtureTearDown();
	}


	protected virtual IEventStoreConnection BuildConnection(MiniNode<TLogFormat, TStreamId> node)
	{
		return TestConnection.Create(node.TcpEndPoint);
	}

	protected async Task CreateUserWithGrpc(
		string loginName,
		string fullName,
		string[] groups,
		string password,
		UserCredentials credentials)
	{
		using var channel = GrpcChannel.ForAddress(new Uri($"https://{_node.HttpEndPoint}"),
			new GrpcChannelOptions {
				HttpClient = _node.HttpClient,
				DisposeHttpClient = false
			});
		var users = new UsersClient(channel);
		await users.CreateAsync(new CreateReq {
			Options = new CreateReq.Types.Options {
				LoginName = loginName,
				FullName = fullName,
				Groups = {groups},
				Password = password
			}
		}, new CallOptions(new Metadata {
			{"authorization", BasicAuthHeader(credentials)}
		}));
	}

	private static string BasicAuthHeader(UserCredentials credentials) =>
		"Basic " + Convert.ToBase64String(
			Encoding.ASCII.GetBytes($"{credentials.Username}:{credentials.Password}"));
}
