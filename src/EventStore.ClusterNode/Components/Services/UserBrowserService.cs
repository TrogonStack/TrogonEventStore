using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
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
			return UserListPage.Unavailable(currentUser, $"Unable to read users: {FriendlyMessage(ex)}");
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
			return UserDetailPage.Unavailable(loginName, currentUser, $"Unable to read user '{loginName}': {FriendlyMessage(ex)}");
		}

		if (!completed.Success)
			return UserDetailPage.Unavailable(loginName, currentUser, FriendlyError(completed.Error));

		return completed.Data is null
			? UserDetailPage.Unavailable(loginName, currentUser, $"User '{loginName}' details were not returned.")
			: UserDetailPage.Success(currentUser, UserView.From(completed.Data));
	}

	private ClaimsPrincipal CurrentUser =>
		httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private Task<bool> HasAccess(Operation operation, CancellationToken cancellationToken) =>
		authorizationProvider.CheckAccessAsync(CurrentUser, operation, cancellationToken).AsTask();

	private static string FriendlyError(UserManagementMessage.Error error) =>
		error switch {
			UserManagementMessage.Error.NotFound => "User was not found.",
			UserManagementMessage.Error.Conflict => "User update conflicted with the current state.",
			UserManagementMessage.Error.TryAgain => "The user store is busy. Try again shortly.",
			UserManagementMessage.Error.Unauthorized => "User access was denied.",
			UserManagementMessage.Error.Error => "The user store returned an error.",
			_ => $"The user store returned {error}."
		};

	private static string FriendlyMessage(Exception ex) =>
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
	public string UpdatedLabel => DateLastUpdated.HasValue
		? $"{DateLastUpdated.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss 'UTC'}"
		: "Never updated";
	public string DetailHref => $"/ui/users/{Uri.EscapeDataString(LoginName)}";
	public string RawHref => $"/users/{Uri.EscapeDataString(LoginName)}";

	public static UserView From(UserManagementMessage.UserData source) =>
		new(
			source.LoginName ?? "",
			source.FullName ?? "",
			source.Groups ?? Array.Empty<string>(),
			source.Disabled,
			source.DateLastUpdated);
}
