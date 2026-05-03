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
		private const string LoginName = "reset-password-target";
		private const string CurrentPassword = "CurrentPa55w0rd!";
		private UsersClient _client;
		private bool _newPasswordAccepted;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(new CreateReq {
				Options = new CreateReq.Types.Options {
					LoginName = LoginName,
					FullName = "Reset Password Target",
					Password = CurrentPassword,
					Groups = { "$ops" }
				}
			}, GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			await _client.ResetPasswordAsync(ResetPasswordRequest(LoginName, "NewPa55w0rd!"),
				GetCallOptions(AdminCredentials));
			await WaitForPasswordToBeAccepted(LoginName, "NewPa55w0rd!", "NewerPa55w0rd!");
			_newPasswordAccepted = true;
		}

		[Test]
		public void succeeds() {
			Assert.IsTrue(_newPasswordAccepted);
		}

		private async Task WaitForPasswordToBeAccepted(string loginName, string currentPassword, string nextPassword) {
			var deadline = DateTime.UtcNow.AddSeconds(5);

			while (true) {
				try {
					await _client.ChangePasswordAsync(ChangePasswordRequest(loginName, currentPassword, nextPassword),
						GetCallOptions((loginName, currentPassword)));
					return;
				} catch (RpcException ex) when (ex.Status.StatusCode == StatusCode.Unauthenticated &&
				                               DateTime.UtcNow < deadline) {
					await Task.Delay(50);
				}
			}
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_resetting_password_without_admin_credentials<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId> {
		private const string LoginName = "reset-password-denied";
		private const string CurrentPassword = "CurrentPa55w0rd!";
		private UsersClient _client;
		private RpcException _exception;

		protected override async Task Given() {
			_client = new UsersClient(Channel);
			await _client.CreateAsync(new CreateReq {
				Options = new CreateReq.Types.Options {
					LoginName = LoginName,
					FullName = "Reset Password Denied",
					Password = CurrentPassword,
					Groups = { "$ops" }
				}
			}, GetCallOptions(AdminCredentials));
		}

		protected override async Task When() {
			try {
				await _client.ResetPasswordAsync(ResetPasswordRequest(LoginName, "NewPa55w0rd!"),
					GetCallOptions((LoginName, CurrentPassword)));
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
