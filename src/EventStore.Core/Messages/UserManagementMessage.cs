using System;
using System.Collections.Generic;
using System.Security.Claims;
using EventStore.Core.Messaging;

namespace EventStore.Core.Messages {
	public static partial class UserManagementMessage {
		[DerivedMessage(CoreMessage.UserManagement)]
		public partial class RequestMessage : Message {
			public readonly IEnvelope Envelope;
			public readonly ClaimsPrincipal Principal;

			public RequestMessage(IEnvelope envelope, ClaimsPrincipal principal) {
				Envelope = envelope;
				Principal = principal;
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public partial class ResponseMessage : Message {
			public readonly bool Success;
			public readonly Error Error;

			public ResponseMessage(bool success, Error error) {
				Success = success;
				Error = error;
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public partial class UserManagementRequestMessage : RequestMessage {
			public readonly string LoginName;

			protected UserManagementRequestMessage(IEnvelope envelope, ClaimsPrincipal principal, string loginName)
				: base(envelope, principal) {
				LoginName = loginName;
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public sealed partial class Create : UserManagementRequestMessage {
			public readonly string FullName;
			public readonly string[] Groups;
			public readonly string Password;

			public Create(
				IEnvelope envelope, ClaimsPrincipal principal, string loginName, string fullName, string[] groups,
				string password)
				: base(envelope, principal, loginName) {
				FullName = fullName;
				Groups = groups;
				Password = password;
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public sealed partial class Update : UserManagementRequestMessage {
			public readonly string FullName;
			public readonly string[] Groups;

			public Update(IEnvelope envelope, ClaimsPrincipal principal, string loginName, string fullName, string[] groups)
				: base(envelope, principal, loginName) {
				FullName = fullName;
				Groups = groups;
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public sealed partial class Disable : UserManagementRequestMessage {
			public Disable(IEnvelope envelope, ClaimsPrincipal principal, string loginName)
				: base(envelope, principal, loginName) {
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public sealed partial class Enable : UserManagementRequestMessage {
			public Enable(IEnvelope envelope, ClaimsPrincipal principal, string loginName)
				: base(envelope, principal, loginName) {
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public sealed partial class Delete : UserManagementRequestMessage {
			public Delete(IEnvelope envelope, ClaimsPrincipal principal, string loginName)
				: base(envelope, principal, loginName) {
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public sealed partial class ResetPassword : UserManagementRequestMessage {
			public readonly string NewPassword;

			public ResetPassword(IEnvelope envelope, ClaimsPrincipal principal, string loginName, string newPassword)
				: base(envelope, principal, loginName) {
				NewPassword = newPassword;
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public sealed partial class ChangePassword : UserManagementRequestMessage {
			public readonly string CurrentPassword;
			public readonly string NewPassword;

			public ChangePassword(
				IEnvelope envelope, ClaimsPrincipal principal, string loginName, string currentPassword, string newPassword)
				: base(envelope, principal, loginName) {
				CurrentPassword = currentPassword;
				NewPassword = newPassword;
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public sealed partial class GetAll : RequestMessage {
			public GetAll(IEnvelope envelope, ClaimsPrincipal principal)
				: base(envelope, principal) {
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public sealed partial class Get : UserManagementRequestMessage {
			public Get(IEnvelope envelope, ClaimsPrincipal principal, string loginName)
				: base(envelope, principal, loginName) {
			}
		}

		public enum Error {
			Success,
			NotFound,
			Conflict,
			Error,
			TryAgain,
			Unauthorized
		}

		public sealed class UserData {
			public readonly string LoginName;
			public readonly string FullName;
			public readonly string[] Groups;
			public readonly DateTimeOffset? DateLastUpdated;
			public readonly bool Disabled;

			public UserData(
				string loginName, string fullName, string[] groups, bool disabled, DateTimeOffset? dateLastUpdated) {
				LoginName = loginName;
				FullName = fullName;
				Groups = groups;
				Disabled = disabled;
				DateLastUpdated = dateLastUpdated;
			}

			internal UserData() {
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public sealed partial class UpdateResult : ResponseMessage {
			public readonly string LoginName;

			public UpdateResult(string loginName)
				: base(true, Error.Success) {
				LoginName = loginName;
			}

			public UpdateResult(string loginName, Error error)
				: base(false, error) {
				LoginName = loginName;
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public sealed partial class UserDetailsResult : ResponseMessage {
			public readonly UserData Data;

			public UserDetailsResult(UserData data)
				: base(true, Error.Success) {
				Data = data;
			}

			public UserDetailsResult(Error error)
				: base(false, error) {
				Data = null;
			}
		}

		[DerivedMessage(CoreMessage.UserManagement)]
		public sealed partial class AllUserDetailsResult : ResponseMessage {
			public readonly UserData[] Data;

			internal AllUserDetailsResult()
				: base(true, Error.Success) {
			}

			public AllUserDetailsResult(UserData[] data)
				: base(true, Error.Success) {
				Data = data;
			}

			public AllUserDetailsResult(Error error)
				: base(false, error) {
				Data = null;
			}
		}
	}
}
