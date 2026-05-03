using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Grpc.Core;
using NUnit.Framework;
using UsersClient = EventStore.Client.Users.Users.UsersClient;

namespace EventStore.Core.Tests.Services.Transport.Grpc.UsersTests;

public class DetailsTests {
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_reading_created_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private DetailsResp _details;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest("details-test-user"), GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			_details = await ReadSingleDetail(_client, "details-test-user", GetCallOptions(AdminCredentials));
		}

		[Test]
		public void returns_the_user_details() {
			Assert.AreEqual("details-test-user", _details.UserDetails.LoginName);
			Assert.AreEqual("Details Test User", _details.UserDetails.FullName);
			Assert.AreEqual(new[] {"admin", "other"}, _details.UserDetails.Groups.ToArray());
			Assert.IsFalse(_details.UserDetails.Disabled);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_reading_missing_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private RpcException _exception;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await Task.CompletedTask;
		}

		protected override async Task When() {
			try {
				await ReadSingleDetail(_client, "missing-details-test-user", GetCallOptions(AdminCredentials));
			} catch (RpcException ex) {
				_exception = ex;
			}
		}

		[Test]
		public void returns_not_found() {
			Assert.IsNotNull(_exception);
			Assert.AreEqual(StatusCode.NotFound, _exception.Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_reading_disabled_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private DetailsResp _details;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest("disabled-details-test-user"), GetCallOptions(AdminCredentials));
			await _client.DisableAsync(DisableRequest("disabled-details-test-user"), GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			_details = await ReadSingleDetail(_client, "disabled-details-test-user", GetCallOptions(AdminCredentials));
		}

		[Test]
		public void returns_the_disabled_state() {
			Assert.IsTrue(_details.UserDetails.Disabled);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_reading_own_user_without_admin_credentials<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId> {
		private const string LoginName = "details-self-test-user";
		private const string Password = "Pa55w0rd!";
		private UsersClient _client;
		private DetailsResp _details;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest(LoginName, Password), GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			_details = await ReadSingleDetail(_client, LoginName, GetCallOptions((LoginName, Password)));
		}

		[Test]
		public void returns_the_user_details() {
			Assert.AreEqual(LoginName, _details.UserDetails.LoginName);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_reading_another_user_without_admin_credentials<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId> {
		private const string ActorLogin = "details-denied-actor";
		private const string TargetLogin = "details-denied-target";
		private const string Password = "Pa55w0rd!";
		private UsersClient _client;
		private RpcException _exception;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest(ActorLogin, Password), GetCallOptions(AdminCredentials));
			await _client.CreateAsync(CreateRequest(TargetLogin, Password), GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			try {
				await ReadSingleDetail(_client, TargetLogin, GetCallOptions((ActorLogin, Password)));
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

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_listing_users_without_admin_credentials<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId> {
		private const string LoginName = "details-list-denied-user";
		private const string Password = "Pa55w0rd!";
		private UsersClient _client;
		private RpcException _exception;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest(LoginName, Password), GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			try {
				await ReadDetails(_client, GetCallOptions((LoginName, Password)));
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

	private static CreateReq CreateRequest(string loginName) =>
		CreateRequest(loginName, "Pa55w0rd!");

	private static CreateReq CreateRequest(string loginName, string password) =>
		new() {
			Options = new CreateReq.Types.Options {
				LoginName = loginName,
				Password = password,
				FullName = "Details Test User",
				Groups = {"admin", "other"}
			}
		};

	private static DisableReq DisableRequest(string loginName) =>
		new() {
			Options = new DisableReq.Types.Options {
				LoginName = loginName
			}
		};

	private static DetailsReq DetailsRequest(string loginName) =>
		new() {
			Options = new DetailsReq.Types.Options {
				LoginName = loginName
			}
		};

	private static DetailsReq DetailsRequest() =>
		new() {
			Options = new DetailsReq.Types.Options()
		};

	private static async Task<DetailsResp> ReadSingleDetail(UsersClient client, string loginName, CallOptions options) {
		using var call = client.Details(DetailsRequest(loginName), options);
		return (await call.ResponseStream.ReadAllAsync().ToArrayAsync()).Single();
	}

	private static async Task<DetailsResp[]> ReadDetails(UsersClient client, CallOptions options) {
		using var call = client.Details(DetailsRequest(), options);
		return await call.ResponseStream.ReadAllAsync().ToArrayAsync();
	}
}
