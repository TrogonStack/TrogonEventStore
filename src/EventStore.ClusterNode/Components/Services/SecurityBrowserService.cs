using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventStore.Plugins.Authentication;
using EventStore.Transport.Http;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class SecurityBrowserService(IAuthenticationProvider authenticationProvider) {
	private static readonly Uri LocalBaseUri = new("http://localhost", UriKind.Absolute);

	public SecurityAuthenticationInfo AuthenticationInfo() {
		var schemes = authenticationProvider.GetSupportedAuthenticationSchemes() ?? [];
		return new SecurityAuthenticationInfo(
			authenticationProvider.Name,
			schemes.Any(x => string.Equals(x, "Basic", StringComparison.OrdinalIgnoreCase)),
			schemes.Any(x => string.Equals(x, "Insecure", StringComparison.OrdinalIgnoreCase)));
	}

	public async Task<SecurityCommandResult> Validate(string username, string password) {
		if (string.IsNullOrWhiteSpace(username))
			return SecurityCommandResult.Failure("Enter a username.");
		if (string.IsNullOrWhiteSpace(password))
			return SecurityCommandResult.Failure("Enter a password.");

		var context = new DefaultHttpContext();
		var request = new HttpAuthenticationRequest(context, username.Trim(), password);
		authenticationProvider.Authenticate(request);
		var (status, _) = await request.AuthenticateAsync();

		return status switch {
			HttpAuthenticationRequestStatus.Authenticated => SecurityCommandResult.Succeeded(),
			HttpAuthenticationRequestStatus.NotReady => SecurityCommandResult.Failure("The authentication provider is not ready yet."),
			HttpAuthenticationRequestStatus.Error => SecurityCommandResult.Failure("The authentication provider failed while checking credentials."),
			_ => SecurityCommandResult.Failure("Incorrect user credentials supplied.")
		};
	}

	public static string NormalizeReturnUrl(string returnUrl) {
		if (string.IsNullOrWhiteSpace(returnUrl))
			return "/ui";

		if (!returnUrl.StartsWith("/", StringComparison.Ordinal) ||
		    returnUrl.StartsWith("//", StringComparison.Ordinal) ||
		    returnUrl.IndexOf('\\') >= 0 ||
		    !Uri.TryCreate(returnUrl, UriKind.Relative, out var relative))
			return "/ui";

		var normalized = new Uri(LocalBaseUri, relative);
		return IsUiPath(normalized.AbsolutePath)
			? $"{normalized.PathAndQuery}{normalized.Fragment}"
			: "/ui";
	}

	public static bool IsSignedIn(ClaimsPrincipal principal) =>
		principal?.Identity?.IsAuthenticated == true;

	private static bool IsUiPath(string path) =>
		path.Equals("/ui", StringComparison.OrdinalIgnoreCase) ||
		path.StartsWith("/ui/", StringComparison.OrdinalIgnoreCase);
}

public sealed record SecurityAuthenticationInfo(string Type, bool SupportsBasic, bool IsInsecure) {
	public string TypeLabel => string.IsNullOrWhiteSpace(Type) ? "External authentication" : Type;
}

public sealed record SecurityCommandResult(bool Success, string Message) {
	public static SecurityCommandResult Succeeded() => new(true, "");
	public static SecurityCommandResult Failure(string message) => new(false, message);
}
