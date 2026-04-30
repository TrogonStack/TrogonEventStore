using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Plugins.Authorization;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class UserBrowserService(
	IPublisher publisher,
	IAuthorizationProvider authorizationProvider,
	IHttpContextAccessor httpContextAccessor) {
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
	private static readonly Operation ListOperation = new(Operations.Users.List);
	private static readonly Operation ReadOperation = new(Operations.Users.Read);
	private static readonly Operation CreateOperation = new(Operations.Users.Create);
	private static readonly Operation UpdateOperation = new(Operations.Users.Update);
	private static readonly Operation DeleteOperation = new(Operations.Users.Delete);
	private static readonly Operation EnableOperation = new(Operations.Users.Enable);
	private static readonly Operation DisableOperation = new(Operations.Users.Disable);
	private static readonly Operation ResetPasswordOperation = new(Operations.Users.ResetPassword);
	public static readonly IReadOnlyList<UserRoleOption> RoleOptions = [
		new("", "No special role"),
		new(SystemRoles.Operations, "Operations"),
		new(SystemRoles.Admins, "Administrator")
	];

	public CurrentUserView ReadCurrentRequest() => CurrentUserView.From(CurrentUser);

	public async Task<UserListPage> ReadAll(CancellationToken cancellationToken = default) {
		var currentUser = CurrentUserView.From(CurrentUser);
		if (!await HasAccess(ListOperation, cancellationToken))
			return UserListPage.Unavailable(currentUser, "User list access was denied.");

		var envelope = new TaskCompletionEnvelope<UserManagementMessage.AllUserDetailsResult>();
		publisher.Publish(new UserManagementMessage.GetAll(envelope, CurrentUser));

		UserManagementMessage.AllUserDetailsResult completed;
		try {
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return UserListPage.Unavailable(currentUser, "Timed out reading users.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return UserListPage.Unavailable(currentUser, $"Unable to read users: {UserInterfaceMessages.Friendly(ex)}");
		}

		return completed.Success
			? UserListPage.Success(currentUser, (completed.Data ?? Array.Empty<UserManagementMessage.UserData>())
				.Select(UserView.From)
				.OrderBy(x => x.LoginName, StringComparer.OrdinalIgnoreCase)
				.ToArray())
			: UserListPage.Unavailable(currentUser, FriendlyError(completed.Error));
	}

	public async Task<UserDetailPage> Read(string loginName, CancellationToken cancellationToken = default) {
		var currentUser = CurrentUserView.From(CurrentUser);
		if (string.IsNullOrWhiteSpace(loginName))
			return UserDetailPage.Unavailable("", currentUser, "Enter a user name to inspect it.");

		var readOperation = ReadOperation.WithParameter(Operations.Users.Parameters.User(loginName));
		if (!await HasAccess(readOperation, cancellationToken))
			return UserDetailPage.Unavailable(loginName, currentUser, $"User '{loginName}' access was denied.");

		var envelope = new TaskCompletionEnvelope<UserManagementMessage.UserDetailsResult>();
		publisher.Publish(new UserManagementMessage.Get(envelope, CurrentUser, loginName));

		UserManagementMessage.UserDetailsResult completed;
		try {
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return UserDetailPage.Unavailable(loginName, currentUser, $"Timed out reading user '{loginName}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return UserDetailPage.Unavailable(loginName, currentUser, $"Unable to read user '{loginName}': {UserInterfaceMessages.Friendly(ex)}");
		}

		if (!completed.Success)
			return UserDetailPage.Unavailable(loginName, currentUser, FriendlyError(completed.Error));

		return completed.Data is null
			? UserDetailPage.Unavailable(loginName, currentUser, $"User '{loginName}' details were not returned.")
			: UserDetailPage.Success(currentUser, UserView.From(completed.Data));
	}

	public async Task<UserCommandResult> Create(UserCreateRequest request, CancellationToken cancellationToken = default) {
		var validation = ValidateCreate(request);
		if (!validation.Success)
			return validation;

		if (!TryReadGroups(request.Role, out var groups))
			return UserCommandResult.Failure(request.LoginName, "Choose a valid user role.");

		var loginName = request.LoginName.Trim();
		var fullName = request.FullName.Trim();
		return await ExecuteCommand(
			loginName,
			CreateOperation,
			envelope => new UserManagementMessage.Create(
				envelope,
				CurrentUser,
				loginName,
				fullName,
				groups,
				request.Password),
			"create",
			$"User '{loginName}' was created.",
			"User creation access was denied.",
			cancellationToken);
	}

	public async Task<UserCommandResult> Update(UserUpdateRequest request, CancellationToken cancellationToken = default) {
		var validation = ValidateUserName(request.LoginName);
		if (!validation.Success)
			return validation;

		if (string.IsNullOrWhiteSpace(request.FullName))
			return UserCommandResult.Failure(request.LoginName, "Enter a full name.");

		if (!TryReadGroups(request.Role, out var selectedGroups))
			return UserCommandResult.Failure(request.LoginName, "Choose a valid user role.");

		var loginName = request.LoginName.Trim();
		var existing = await Read(loginName, cancellationToken);
		if (existing.User is null)
			return UserCommandResult.Failure(loginName, existing.Message);

		var groups = MergeRoleWithCustomGroups(existing.User.Groups, selectedGroups);
		return await ExecuteCommand(
			loginName,
			UpdateOperation.WithParameter(Operations.Users.Parameters.User(loginName)),
			envelope => new UserManagementMessage.Update(envelope, CurrentUser, loginName, request.FullName.Trim(), groups),
			"update",
			$"User '{loginName}' was updated.",
			$"User '{loginName}' update access was denied.",
			cancellationToken);
	}

	public async Task<UserCommandResult> Enable(string loginName, CancellationToken cancellationToken = default) {
		var validation = ValidateUserName(loginName);
		if (!validation.Success)
			return validation;

		loginName = loginName.Trim();
		return await ExecuteCommand(
			loginName,
			EnableOperation.WithParameter(Operations.Users.Parameters.User(loginName)),
			envelope => new UserManagementMessage.Enable(envelope, CurrentUser, loginName),
			"enable",
			$"User '{loginName}' was enabled.",
			$"User '{loginName}' enable access was denied.",
			cancellationToken);
	}

	public async Task<UserCommandResult> Disable(string loginName, CancellationToken cancellationToken = default) {
		var validation = ValidateUserName(loginName);
		if (!validation.Success)
			return validation;

		loginName = loginName.Trim();
		return await ExecuteCommand(
			loginName,
			DisableOperation.WithParameter(Operations.Users.Parameters.User(loginName)),
			envelope => new UserManagementMessage.Disable(envelope, CurrentUser, loginName),
			"disable",
			$"User '{loginName}' was disabled.",
			$"User '{loginName}' disable access was denied.",
			cancellationToken);
	}

	public async Task<UserCommandResult> Delete(string loginName, CancellationToken cancellationToken = default) {
		var validation = ValidateUserName(loginName);
		if (!validation.Success)
			return validation;

		loginName = loginName.Trim();
		return await ExecuteCommand(
			loginName,
			DeleteOperation.WithParameter(Operations.Users.Parameters.User(loginName)),
			envelope => new UserManagementMessage.Delete(envelope, CurrentUser, loginName),
			"delete",
			$"User '{loginName}' was deleted.",
			$"User '{loginName}' delete access was denied.",
			cancellationToken);
	}

	public async Task<UserCommandResult> ResetPassword(UserPasswordResetRequest request, CancellationToken cancellationToken = default) {
		var validation = ValidateUserName(request.LoginName);
		if (!validation.Success)
			return validation;

		if (string.IsNullOrWhiteSpace(request.Password))
			return UserCommandResult.Failure(request.LoginName, "Enter a new password.");

		if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
			return UserCommandResult.Failure(request.LoginName, "The password confirmation does not match.");

		var loginName = request.LoginName.Trim();
		return await ExecuteCommand(
			loginName,
			ResetPasswordOperation.WithParameter(Operations.Users.Parameters.User(loginName)),
			envelope => new UserManagementMessage.ResetPassword(envelope, CurrentUser, loginName, request.Password),
			"reset password for",
			$"Password for '{loginName}' was reset.",
			$"Password reset access for '{loginName}' was denied.",
			cancellationToken);
	}

	private ClaimsPrincipal CurrentUser =>
		httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private Task<bool> HasAccess(Operation operation, CancellationToken cancellationToken) =>
		authorizationProvider.CheckAccessAsync(CurrentUser, operation, cancellationToken).AsTask();

	private async Task<UserCommandResult> ExecuteCommand(
		string loginName,
		Operation operation,
		Func<IEnvelope, UserManagementMessage.UserManagementRequestMessage> createMessage,
		string actionDescription,
		string successMessage,
		string deniedMessage,
		CancellationToken cancellationToken) {
		if (!await HasAccess(operation, cancellationToken))
			return UserCommandResult.Failure(loginName, deniedMessage);

		var envelope = new TaskCompletionEnvelope<UserManagementMessage.UpdateResult>();
		UserManagementMessage.UpdateResult completed;
		try {
			publisher.Publish(createMessage(envelope));
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return UserCommandResult.Failure(loginName, $"Timed out trying to {actionDescription} user '{loginName}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return UserCommandResult.Failure(loginName, $"Unable to {actionDescription} user '{loginName}': {UserInterfaceMessages.Friendly(ex)}");
		}

		return completed.Success
			? UserCommandResult.Succeeded(completed.LoginName ?? loginName, successMessage)
			: UserCommandResult.Failure(completed.LoginName ?? loginName, FriendlyError(completed.Error));
	}

	private static UserCommandResult ValidateCreate(UserCreateRequest request) {
		var validation = ValidateUserName(request.LoginName);
		if (!validation.Success)
			return validation;

		if (string.IsNullOrWhiteSpace(request.FullName))
			return UserCommandResult.Failure(request.LoginName, "Enter a full name.");

		if (string.IsNullOrWhiteSpace(request.Password))
			return UserCommandResult.Failure(request.LoginName, "Enter a password.");

		if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
			return UserCommandResult.Failure(request.LoginName, "The password confirmation does not match.");

		return UserCommandResult.Succeeded(request.LoginName.Trim(), "");
	}

	private static UserCommandResult ValidateUserName(string loginName) =>
		string.IsNullOrWhiteSpace(loginName)
			? UserCommandResult.Failure("", "Enter a login name.")
			: UserCommandResult.Succeeded(loginName.Trim(), "");

	private static bool TryReadGroups(string role, out string[] groups) {
		role = role?.Trim() ?? "";
		groups = role switch {
			"" => Array.Empty<string>(),
			SystemRoles.Operations => [SystemRoles.Operations],
			SystemRoles.Admins => [SystemRoles.Admins],
			_ => Array.Empty<string>()
		};

		return role is "" or SystemRoles.Operations or SystemRoles.Admins;
	}

	private static string[] MergeRoleWithCustomGroups(IReadOnlyList<string> currentGroups, string[] selectedGroups) =>
		currentGroups
			.Where(x => !IsBuiltInRole(x))
			.Concat(selectedGroups)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
			.ToArray();

	private static bool IsBuiltInRole(string group) =>
		string.Equals(group, SystemRoles.Operations, StringComparison.OrdinalIgnoreCase) ||
		string.Equals(group, SystemRoles.Admins, StringComparison.OrdinalIgnoreCase);

	private static string FriendlyError(UserManagementMessage.Error error) =>
		error switch {
			UserManagementMessage.Error.NotFound => "User was not found.",
			UserManagementMessage.Error.Conflict => "User update conflicted with the current state.",
			UserManagementMessage.Error.TryAgain => "The user store is busy. Try again shortly.",
			UserManagementMessage.Error.Unauthorized => "User access was denied.",
			UserManagementMessage.Error.Error => "The user store returned an error.",
			_ => $"The user store returned {error}."
		};

}

public static class UserInterfaceMessages {
	public static string Friendly(Exception ex) =>
		string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}

public sealed record UserListPage(
	CurrentUserView CurrentUser,
	IReadOnlyList<UserView> Users,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public bool HasUsers => Users.Count > 0;
	public int EnabledCount => Users.Count(x => !x.Disabled);
	public int DisabledCount => Users.Count(x => x.Disabled);
	public int GroupCount => Users.SelectMany(x => x.Groups).Distinct(StringComparer.OrdinalIgnoreCase).Count();
	public string UserCountLabel => IsAvailable ? Users.Count.ToString() : "-";
	public string EnabledCountLabel => IsAvailable ? EnabledCount.ToString() : "-";
	public string DisabledCountLabel => IsAvailable ? DisabledCount.ToString() : "-";
	public string GroupCountLabel => IsAvailable ? GroupCount.ToString() : "-";

	public static UserListPage Success(CurrentUserView currentUser, IReadOnlyList<UserView> users) => new(currentUser, users, "");
	public static UserListPage Unavailable(CurrentUserView currentUser, string message) => new(currentUser, Array.Empty<UserView>(), message);
}

#nullable enable
public sealed record UserDetailPage(
	CurrentUserView CurrentUser,
	UserView? User,
	string LoginName,
	string Message) {
	public bool HasUser => User is not null;

	public static UserDetailPage Success(CurrentUserView currentUser, UserView user) => new(currentUser, user, user.LoginName, "");
	public static UserDetailPage Unavailable(string loginName, CurrentUserView currentUser, string message) => new(currentUser, null, loginName, message);
}
#nullable restore

public sealed record UserCommandResult(
	bool Success,
	string LoginName,
	string Message) {
	public static UserCommandResult Succeeded(string loginName, string message) => new(true, loginName, message);
	public static UserCommandResult Failure(string loginName, string message) => new(false, loginName, message);
}

public sealed record UserCreateRequest(
	string LoginName,
	string FullName,
	string Role,
	string Password,
	string ConfirmPassword);

public sealed record UserUpdateRequest(
	string LoginName,
	string FullName,
	string Role);

public sealed record UserPasswordResetRequest(
	string LoginName,
	string Password,
	string ConfirmPassword);

public sealed record UserRoleOption(
	string Value,
	string Label);

public sealed record CurrentUserView(
	string Name,
	string AuthenticationType,
	bool IsAuthenticated,
	IReadOnlyList<string> Roles) {
	public string NameLabel => string.IsNullOrWhiteSpace(Name) ? "Anonymous" : Name;
	public string AuthenticationLabel => IsAuthenticated ? "Authenticated" : "Anonymous";
	public string AuthenticationTone => IsAuthenticated ? "good" : "muted";
	public string RolesLabel => Roles.Count == 0 ? "No roles" : string.Join(", ", Roles);

	public static CurrentUserView From(ClaimsPrincipal principal) {
		var identity = principal.Identity;
		var roles = principal.Claims
			.Where(x => x.Type == ClaimTypes.Role || string.Equals(x.Type, "role", StringComparison.OrdinalIgnoreCase))
			.Select(x => x.Value)
			.Where(x => !string.IsNullOrWhiteSpace(x))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		return new(
			identity?.Name ?? "",
			identity?.AuthenticationType ?? "",
			identity?.IsAuthenticated == true,
			roles);
	}
}

public sealed record UserView(
	string LoginName,
	string FullName,
	IReadOnlyList<string> Groups,
	bool Disabled,
	DateTimeOffset? DateLastUpdated) {
	public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? LoginName : FullName;
	public string StatusLabel => Disabled ? "Disabled" : "Enabled";
	public string StatusTone => Disabled ? "muted" : "good";
	public string GroupsLabel => Groups.Count == 0 ? "No groups" : string.Join(", ", Groups);
	public string SelectedRole => Groups.Contains(SystemRoles.Admins, StringComparer.OrdinalIgnoreCase)
		? SystemRoles.Admins
		: Groups.Contains(SystemRoles.Operations, StringComparer.OrdinalIgnoreCase)
			? SystemRoles.Operations
			: "";
	public string UpdatedLabel => DateLastUpdated.HasValue
		? $"{DateLastUpdated.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss 'UTC'}"
		: "Never updated";
	public string DetailHref => $"/ui/users/{Uri.EscapeDataString(LoginName)}";
	public string EditHref => $"/ui/users/{Uri.EscapeDataString(LoginName)}/edit";
	public string EnableHref => $"/ui/users/{Uri.EscapeDataString(LoginName)}/enable";
	public string DisableHref => $"/ui/users/{Uri.EscapeDataString(LoginName)}/disable";
	public string DeleteHref => $"/ui/users/{Uri.EscapeDataString(LoginName)}/delete";
	public string ResetPasswordHref => $"/ui/users/{Uri.EscapeDataString(LoginName)}/reset";

	public static UserView From(UserManagementMessage.UserData source) =>
		new(
			source.LoginName ?? "",
			source.FullName ?? "",
			source.Groups ?? Array.Empty<string>(),
			source.Disabled,
			source.DateLastUpdated);
}
