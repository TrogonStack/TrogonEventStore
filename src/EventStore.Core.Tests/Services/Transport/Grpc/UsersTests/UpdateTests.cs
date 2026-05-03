using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Grpc.Core;
using NUnit.Framework;
using UsersClient = EventStore.Client.Users.Users.UsersClient;

namespace EventStore.Core.Tests.Services.Transport.Grpc.UsersTests;

public class UpdateTests {
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_updating_existing_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private DetailsResp _details;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest("update-test-user"), GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			await _client.UpdateAsync(UpdateRequest("update-test-user"), GetCallOptions(AdminCredentials));
			_details = await ReadSingleDetail(_client, "update-test-user", GetCallOptions(AdminCredentials));
		}

		[Test]
		public void stores_the_updated_user_details() {
			Assert.AreEqual("update-test-user", _details.UserDetails.LoginName);
			Assert.AreEqual("Updated Test User", _details.UserDetails.FullName);
			Assert.AreEqual(new[] {"ops", "other"}, _details.UserDetails.Groups.ToArray());
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_updating_missing_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private RpcException _exception;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await Task.CompletedTask;
		}

		protected override async Task When() {
			try {
				await _client.UpdateAsync(UpdateRequest("missing-update-test-user"), GetCallOptions(AdminCredentials));
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
				FullName = "Update Test User",
				Groups = {"admin", "other"}
			}
		};

	private static UpdateReq UpdateRequest(string loginName) =>
		new() {
			Options = new UpdateReq.Types.Options {
				LoginName = loginName,
				FullName = "Updated Test User",
				Groups = {"ops", "other"}
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
