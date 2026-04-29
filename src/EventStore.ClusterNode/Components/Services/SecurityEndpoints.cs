using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EventStore.ClusterNode.Components.Services;

internal static class SecurityEndpoints {
	private const string MigrationTokenCookieName = "es-ui-migration-token";

	public static IEndpointRouteBuilder MapSecurityEndpoints(this IEndpointRouteBuilder app) {
		app.MapPost("/ui/security/migrate-credentials", async (HttpContext context, SecurityBrowserService security) => {
			SecurityCredentialMigration request;
			try {
				request = await ReadMigrationRequest(context);
			} catch (Exception ex) when (ex is BadHttpRequestException or JsonException) {
				return Results.BadRequest();
			}

			if (request is null)
				return Results.BadRequest();

			if (!IsSameOrigin(context.Request) && !HasValidMigrationToken(context, request))
				return Results.StatusCode(StatusCodes.Status403Forbidden);

			DeleteMigrationToken(context.Response);

			if (string.IsNullOrWhiteSpace(request.Credentials))
				return Results.BadRequest();

			if (!UiCredentialCookie.TryParseBasicCredentials(request.Credentials, out var credentials))
				return request.UsesRedirect
					? ClearLegacyCredentialsAndRedirect(context, request.ReturnUrl)
					: Results.BadRequest();

			SecurityCommandResult validation;
			try {
				validation = await security.Validate(credentials.Username, credentials.Password);
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception) {
				return request.UsesRedirect
					? ClearLegacyCredentialsAndRedirect(context, request.ReturnUrl)
					: Results.Unauthorized();
			}

			if (!validation.Success)
				return request.UsesRedirect
					? ClearLegacyCredentialsAndRedirect(context, request.ReturnUrl)
					: Results.Unauthorized();

			UiCredentialCookie.AppendBasic(context.Response, credentials);
			return RedirectToReturnUrlOrNoContent(request.ReturnUrl);
		});

		return app;
	}

	private static async Task<SecurityCredentialMigration> ReadMigrationRequest(HttpContext context) {
		if (context.Request.HasFormContentType) {
			var form = await context.Request.ReadFormAsync(context.RequestAborted);
			return new SecurityCredentialMigration(
				form["credentials"].ToString(),
				NormalizeOptionalReturnUrl(form["returnUrl"].ToString()),
				form["migrationToken"].ToString(),
				UsesRedirect: true);
		}

		var request = await context.Request.ReadFromJsonAsync<SecurityCredentialMigrationRequest>(
			cancellationToken: context.RequestAborted);

		return request is null
			? null
			: new SecurityCredentialMigration(
				request.Credentials ?? "",
				NormalizeOptionalReturnUrl(request.ReturnUrl),
				request.MigrationToken ?? "",
				UsesRedirect: false);
	}

	private static IResult ClearLegacyCredentialsAndRedirect(HttpContext context, string returnUrl) {
		UiCredentialCookie.Delete(context.Response);
		DeleteMigrationToken(context.Response);
		var normalizedReturnUrl = NormalizeOptionalReturnUrl(returnUrl);
		return Results.Redirect(string.IsNullOrWhiteSpace(normalizedReturnUrl) ? "/ui" : normalizedReturnUrl);
	}

	private static IResult RedirectToReturnUrlOrNoContent(string returnUrl) {
		var normalizedReturnUrl = NormalizeOptionalReturnUrl(returnUrl);
		return string.IsNullOrWhiteSpace(normalizedReturnUrl)
			? Results.NoContent()
			: Results.Redirect(normalizedReturnUrl);
	}

	private static string NormalizeOptionalReturnUrl(string returnUrl) =>
		string.IsNullOrWhiteSpace(returnUrl)
			? ""
			: SecurityBrowserService.NormalizeReturnUrl(returnUrl);

	private static bool IsSameOrigin(HttpRequest request) {
		if (!TryReadOrigin(request, out var origin))
			return false;

		var expectedPort = request.Host.Port ?? DefaultPort(request.Scheme);
		return string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
		       string.Equals(origin.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase) &&
		       origin.Port == expectedPort;
	}

	private static bool TryReadOrigin(HttpRequest request, out Uri origin) {
		if (request.Headers.TryGetValue("Origin", out var originHeader) &&
		    Uri.TryCreate(originHeader.ToString(), UriKind.Absolute, out origin))
			return true;

		if (request.Headers.TryGetValue("Referer", out var refererHeader) &&
		    Uri.TryCreate(refererHeader.ToString(), UriKind.Absolute, out origin))
			return true;

		origin = null;
		return false;
	}

	private static int DefaultPort(string scheme) =>
		string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;

	private static bool HasValidMigrationToken(HttpContext context, SecurityCredentialMigration request) =>
		request.UsesRedirect &&
		request.MigrationToken.Length >= 16 &&
		context.Request.Cookies.TryGetValue(MigrationTokenCookieName, out var cookieToken) &&
		string.Equals(cookieToken, request.MigrationToken, StringComparison.Ordinal);

	private static void DeleteMigrationToken(HttpResponse response) =>
		response.Cookies.Delete(MigrationTokenCookieName, new CookieOptions {
			HttpOnly = false,
			IsEssential = true,
			Path = "/",
			SameSite = SameSiteMode.Lax,
			Secure = response.HttpContext.Request.IsHttps
		});
}

internal sealed record SecurityCredentialMigrationRequest(string Credentials, string ReturnUrl = "", string MigrationToken = "");
internal sealed record SecurityCredentialMigration(string Credentials, string ReturnUrl, string MigrationToken, bool UsesRedirect);
