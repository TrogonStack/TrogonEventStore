using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class SecurityBrowserService(IAuthenticationProvider authenticationProvider, bool supportsPassword)
{
	private static readonly Uri LocalBaseUri = new("http://localhost", UriKind.Absolute);

	public SecurityAuthenticationInfo AuthenticationInfo()
	{
		var schemes = authenticationProvider.GetSupportedAuthenticationSchemes() ?? [];
		var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (key, value) in authenticationProvider.GetPublicProperties() ?? [])
		{
			properties[key] = value;
		}

		return new SecurityAuthenticationInfo(
			authenticationProvider.Name,
			supportsPassword,
			schemes.Any(x => string.Equals(x, "Bearer", StringComparison.OrdinalIgnoreCase)) &&
			properties.ContainsKey("authorization_endpoint") &&
			properties.ContainsKey("client_id") &&
			properties.ContainsKey("code_challenge_uri") &&
			properties.ContainsKey("redirect_uri") &&
			properties.ContainsKey("response_type") &&
			properties.TryGetValue("scope", out var scope) &&
			!string.IsNullOrWhiteSpace(scope),
			schemes.Any(x => string.Equals(x, "Insecure", StringComparison.OrdinalIgnoreCase)),
			properties);
	}

	public async Task<SecurityCommandResult> Validate(string username, string password)
	{
		if (string.IsNullOrWhiteSpace(username))
		{
			return SecurityCommandResult.Failure("Enter a username.");
		}

		if (string.IsNullOrWhiteSpace(password))
		{
			return SecurityCommandResult.Failure("Enter a password.");
		}

		var context = new DefaultHttpContext();
		var request = new HttpAuthenticationRequest(context, username.Trim(), password);
		authenticationProvider.Authenticate(request);
		var (status, _) = await request.AuthenticateAsync();

		return status switch
		{
			HttpAuthenticationRequestStatus.Authenticated => SecurityCommandResult.Succeeded(),
			HttpAuthenticationRequestStatus.NotReady => SecurityCommandResult.Failure("The authentication provider is not ready yet."),
			HttpAuthenticationRequestStatus.Error => SecurityCommandResult.Failure("The authentication provider failed while checking credentials."),
			_ => SecurityCommandResult.Failure("Incorrect user credentials supplied.")
		};
	}

	public static string NormalizeReturnUrl(string returnUrl)
	{
		if (string.IsNullOrWhiteSpace(returnUrl))
		{
			return "/ui";
		}

		if (!returnUrl.StartsWith("/", StringComparison.Ordinal) ||
			returnUrl.StartsWith("//", StringComparison.Ordinal) ||
			returnUrl.IndexOf('\\') >= 0 ||
			!Uri.TryCreate(returnUrl, UriKind.Relative, out var relative))
		{
			return "/ui";
		}

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

public sealed record SecurityAuthenticationInfo(
	string Type,
	bool SupportsBasic,
	bool SupportsOAuthBrowserFlow,
	bool IsInsecure,
	IReadOnlyDictionary<string, string> Properties)
{
	public static SecurityAuthenticationInfo Unavailable() =>
		new("Unavailable", SupportsBasic: false, SupportsOAuthBrowserFlow: false, IsInsecure: false, new Dictionary<string, string>());

	public string TypeLabel => string.IsNullOrWhiteSpace(Type) ? "External authentication" : Type;
	public string PropertiesJson => JsonSerializer.Serialize(Properties);
}

public sealed record SecurityCommandResult(bool Success, string Message)
{
	public static SecurityCommandResult Succeeded() => new(true, "");
	public static SecurityCommandResult Failure(string message) => new(false, message);
}
