using System;
using System.Threading.Tasks;
using EventStore.Client.Users;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Grpc.Core;
using NUnit.Framework;
using UsersClient = EventStore.Client.Users.Users.UsersClient;

namespace EventStore.Core.Tests.Services.Transport.Grpc.UsersTests;

public class ResetPasswordTests {
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_resetting_password_with_admin_credentials<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private bool _newPasswordAccepted;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await Task.CompletedTask;
		}

		protected override async Task When() {
			await _client.ResetPasswordAsync(ResetPasswordRequest("admin", "NewPa55w0rd!"),
				GetCallOptions(AdminCredentials));
			await WaitForPasswordToBeAccepted("NewPa55w0rd!", "NewerPa55w0rd!");
			_newPasswordAccepted = true;
		}

		[Test]
		public void succeeds() {
			Assert.IsTrue(_newPasswordAccepted);
		}

		private async Task WaitForPasswordToBeAccepted(string currentPassword, string nextPassword) {
			var deadline = DateTime.UtcNow.AddSeconds(5);

			while (true) {
				try {
					await _client.ChangePasswordAsync(ChangePasswordRequest("admin", currentPassword, nextPassword),
						GetCallOptions(("admin", currentPassword)));
					return;
				} catch (RpcException ex) when (ex.Status.StatusCode == StatusCode.Unauthenticated &&
				                               DateTime.UtcNow < deadline) {
					await Task.Delay(50);
				}
			}
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_resetting_password_for_missing_user<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId> {
		private UsersClient _client;
		private RpcException _exception;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await Task.CompletedTask;
		}

		protected override async Task When() {
			try {
				await _client.ResetPasswordAsync(ResetPasswordRequest("missing", "NewPa55w0rd!"),
					GetCallOptions(AdminCredentials));
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

	private static ResetPasswordReq ResetPasswordRequest(string loginName, string newPassword) =>
		new() {
			Options = new ResetPasswordReq.Types.Options {
				LoginName = loginName,
				NewPassword = newPassword
			}
		};

	private static ChangePasswordReq ChangePasswordRequest(string loginName, string currentPassword, string newPassword) =>
		new() {
			Options = new ChangePasswordReq.Types.Options {
				LoginName = loginName,
				CurrentPassword = currentPassword,
				NewPassword = newPassword
			}
		};
}
