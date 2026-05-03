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
	public class when_reading_all_users<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private DetailsResp[] _details;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest("details-list-test-user-1"), GetCallOptions(AdminCredentials));
			await _client.CreateAsync(CreateRequest("details-list-test-user-2"), GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			using var call = _client.Details(new DetailsReq(), GetCallOptions(AdminCredentials));
			_details = await call.ResponseStream.ReadAllAsync().ToArrayAsync();
		}

		[Test]
		public void streams_the_user_list() {
			var loginNames = _details.Select(x => x.UserDetails.LoginName).ToArray();
			CollectionAssert.Contains(loginNames, "details-list-test-user-1");
			CollectionAssert.Contains(loginNames, "details-list-test-user-2");
		}
	}

	private static CreateReq CreateRequest(string loginName) =>
		new() {
			Options = new CreateReq.Types.Options {
				LoginName = loginName,
				Password = "Pa55w0rd!",
				FullName = "Details Test User",
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
