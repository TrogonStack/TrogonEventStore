using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Grpc.Core;
using NUnit.Framework;
using UsersClient = EventStore.Client.Users.Users.UsersClient;

namespace EventStore.Core.Tests.Services.Transport.Grpc.UsersTests;

public class DeleteTests {
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_deleting_created_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private RpcException _detailsException;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(CreateRequest("delete-test-user"), GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			await _client.DeleteAsync(DeleteRequest("delete-test-user"), GetCallOptions(AdminCredentials));

			try {
				await ReadDetails(_client, "delete-test-user", GetCallOptions(AdminCredentials));
			} catch (RpcException ex) {
				_detailsException = ex;
			}
		}

		[Test]
		public void removes_the_user() {
			Assert.IsNotNull(_detailsException);
			Assert.AreEqual(StatusCode.NotFound, _detailsException.Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_deleting_missing_user<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private RpcException _exception;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await Task.CompletedTask;
		}

		protected override async Task When() {
			try {
				await _client.DeleteAsync(DeleteRequest("missing-delete-test-user"), GetCallOptions(AdminCredentials));
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
				FullName = "Delete Test User",
				Groups = {"admin", "other"}
			}
		};

	private static DeleteReq DeleteRequest(string loginName) =>
		new() {
			Options = new DeleteReq.Types.Options {
				LoginName = loginName
			}
		};

	private static DetailsReq DetailsRequest(string loginName) =>
		new() {
			Options = new DetailsReq.Types.Options {
				LoginName = loginName
			}
		};

	private static async Task<DetailsResp[]> ReadDetails(UsersClient client, string loginName, CallOptions options) {
		using var call = client.Details(DetailsRequest(loginName), options);
		return await call.ResponseStream.ReadAllAsync().ToArrayAsync();
	}
}
