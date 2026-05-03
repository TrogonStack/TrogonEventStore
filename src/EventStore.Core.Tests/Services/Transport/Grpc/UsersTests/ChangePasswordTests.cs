using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Grpc.Core;
using NUnit.Framework;
using UsersClient = EventStore.Client.Users.Users.UsersClient;

namespace EventStore.Core.Tests.Services.Transport.Grpc.UsersTests;

public class ChangePasswordTests {
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_changing_password_with_correct_current_password<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private bool _completed;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await Task.CompletedTask;
		}

		protected override async Task When() {
			await _client.ChangePasswordAsync(ChangePasswordRequest("admin", "changeit", "NewPa55w0rd!"),
				GetCallOptions(AdminCredentials));
			_completed = true;
		}

		[Test]
		public void succeeds() {
			Assert.IsTrue(_completed);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_changing_password_with_incorrect_current_password<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private RpcException _exception;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await Task.CompletedTask;
		}

		protected override async Task When() {
			try {
				await _client.ChangePasswordAsync(ChangePasswordRequest("admin", "WrongPa55w0rd!", "NewPa55w0rd!"),
					GetCallOptions(AdminCredentials));
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

	private static ChangePasswordReq ChangePasswordRequest(string loginName, string currentPassword, string newPassword) =>
		new() {
			Options = new ChangePasswordReq.Types.Options {
				LoginName = loginName,
				CurrentPassword = currentPassword,
				NewPassword = newPassword
			}
		};
}
