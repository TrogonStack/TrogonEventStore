using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Grpc.Core;
using NUnit.Framework;
using UsersClient = EventStore.Client.Users.Users.UsersClient;

namespace EventStore.Core.Tests.Services.Transport.Grpc.UsersTests;

public class CreateTests {
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_creating_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private DetailsResp _details;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await Task.CompletedTask;
		}

		protected override async Task When() {
			await _client.CreateAsync(CreateRequest("create-test-user", "Create Test User", "Pa55w0rd!"),
				GetCallOptions(AdminCredentials));
			_details = await ReadSingleDetail(_client, "create-test-user", GetCallOptions(AdminCredentials));
		}

		[Test]
		public void stores_the_user_details() {
			Assert.AreEqual("create-test-user", _details.UserDetails.LoginName);
			Assert.AreEqual("Create Test User", _details.UserDetails.FullName);
			Assert.AreEqual(new[] {"admin", "other"}, _details.UserDetails.Groups.ToArray());
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_creating_user_with_unicode_name<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private DetailsResp _details;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await Task.CompletedTask;
		}

		protected override async Task When() {
			await _client.CreateAsync(CreateRequest("码ou£ro码", "Unicode Test User", "ou码码ro"),
				GetCallOptions(AdminCredentials));
			_details = await ReadSingleDetail(_client, "码ou£ro码", GetCallOptions(AdminCredentials));
		}

		[Test]
		public void stores_the_user_details() {
			Assert.AreEqual("码ou£ro码", _details.UserDetails.LoginName);
			Assert.AreEqual("Unicode Test User", _details.UserDetails.FullName);
			Assert.AreEqual(new[] {"admin", "other"}, _details.UserDetails.Groups.ToArray());
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_creating_existing_user_with_same_password<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private DetailsResp _details;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest("create-idempotent-test-user", "Create Test User", "Pa55w0rd!"),
				GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			await _client.CreateAsync(CreateRequest("create-idempotent-test-user", "Create Test User", "Pa55w0rd!"),
				GetCallOptions(AdminCredentials));
			_details = await ReadSingleDetail(_client, "create-idempotent-test-user", GetCallOptions(AdminCredentials));
		}

		[Test]
		public void leaves_the_existing_user_available() {
			Assert.AreEqual("create-idempotent-test-user", _details.UserDetails.LoginName);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_creating_existing_user_with_different_password<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private RpcException _exception;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest("create-conflict-test-user", "Create Test User", "Pa55w0rd!"),
				GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			try {
				await _client.CreateAsync(
					CreateRequest("create-conflict-test-user", "Create Test User", "AnotherPa55w0rd!"),
					GetCallOptions(AdminCredentials));
			} catch (RpcException ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_failed_precondition() {
			Assert.IsNotNull(_exception);
			Assert.AreEqual(StatusCode.FailedPrecondition, _exception.Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_creating_user_without_admin_credentials<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId> {
		private const string ActorLogin = "create-denied-actor";
		private const string Password = "Pa55w0rd!";
		private UsersClient _client;
		private RpcException _exception;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest(ActorLogin, "Create Denied Actor", Password),
				GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			try {
				await _client.CreateAsync(CreateRequest("create-denied-target", "Create Denied Target", Password),
					GetCallOptions((ActorLogin, Password)));
			} catch (RpcException ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_permission_denied() {
			Assert.IsNotNull(_exception);
			Assert.AreEqual(StatusCode.PermissionDenied, _exception.Status.StatusCode);
		}
	}

	private static CreateReq CreateRequest(string loginName, string fullName, string password) =>
		new() {
			Options = new CreateReq.Types.Options {
				LoginName = loginName,
				Password = password,
				FullName = fullName,
				Groups = {"admin", "other"}
			}
		};

	private static DetailsReq DetailsRequest(string loginName) =>
		new() {
			Options = new DetailsReq.Types.Options {
				LoginName = loginName
			}
		};

	private static async Task<DetailsResp> ReadSingleDetail(UsersClient client, string loginName, CallOptions options) {
		using var call = client.Details(DetailsRequest(loginName), options);
		return (await call.ResponseStream.ReadAllAsync().ToArrayAsync()).Single();
	}
}
