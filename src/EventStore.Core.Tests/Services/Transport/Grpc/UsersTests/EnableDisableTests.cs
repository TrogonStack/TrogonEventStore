using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Grpc.Core;
using NUnit.Framework;
using UsersClient = EventStore.Client.Users.Users.UsersClient;

namespace EventStore.Core.Tests.Services.Transport.Grpc.UsersTests;

public class EnableDisableTests {
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_disabling_enabled_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private DetailsResp _details;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest("disable-test-user"), GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			await _client.DisableAsync(DisableRequest("disable-test-user"), GetCallOptions(AdminCredentials));
			_details = await ReadSingleDetail(_client, "disable-test-user", GetCallOptions(AdminCredentials));
		}

		[Test]
		public void marks_the_user_disabled() {
			Assert.IsTrue(_details.UserDetails.Disabled);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_enabling_disabled_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private DetailsResp _details;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest("enable-test-user"), GetCallOptions(AdminCredentials));
			await _client.DisableAsync(DisableRequest("enable-test-user"), GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			await _client.EnableAsync(EnableRequest("enable-test-user"), GetCallOptions(AdminCredentials));
			_details = await ReadSingleDetail(_client, "enable-test-user", GetCallOptions(AdminCredentials));
		}

		[Test]
		public void marks_the_user_enabled() {
			Assert.IsFalse(_details.UserDetails.Disabled);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_disabling_missing_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private RpcException _exception;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await Task.CompletedTask;
		}

		protected override async Task When() {
			try {
				await _client.DisableAsync(DisableRequest("missing-disable-test-user"), GetCallOptions(AdminCredentials));
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
	public class when_enabling_missing_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private RpcException _exception;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await Task.CompletedTask;
		}

		protected override async Task When() {
			try {
				await _client.EnableAsync(EnableRequest("missing-enable-test-user"), GetCallOptions(AdminCredentials));
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

	private static CreateReq CreateRequest(string loginName) =>
		new() {
			Options = new CreateReq.Types.Options {
				LoginName = loginName,
				Password = "Pa55w0rd!",
				FullName = "Enable Disable Test User",
				Groups = {"admin", "other"}
			}
		};

	private static DisableReq DisableRequest(string loginName) =>
		new() {
			Options = new DisableReq.Types.Options {
				LoginName = loginName
			}
		};

	private static EnableReq EnableRequest(string loginName) =>
		new() {
			Options = new EnableReq.Types.Options {
				LoginName = loginName
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
