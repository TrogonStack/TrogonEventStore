using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core;
using EventStore.Core.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EventStore.ClusterNode.Components.Services;

internal static class OAuthBrowserFlowEndpoints
{
	public static IEndpointRouteBuilder MapOAuthBrowserFlowEndpoints(
		this IEndpointRouteBuilder app,
		ClusterVNodeOptions.OAuthOptions options)
	{
		app.MapGet(options.CodeChallengePath, (HttpContext context, OAuthBrowserFlowService service) =>
			Results.Json(service.CreateCodeChallenge(context), OAuthBrowserFlowService.JsonOptions));

		app.MapGet(options.RedirectPath, async (
			HttpContext context,
			OAuthBrowserFlowService service) =>
			await service.HandleCallback(context, context.RequestAborted));

		return app;
	}
}

public sealed class OAuthBrowserFlowService(
	ClusterVNodeOptions.OAuthOptions options,
	HttpClient httpClient,
	TimeProvider timeProvider,
	IDataProtectionProvider dataProtectionProvider,
	OAuthTokenValidator tokenValidator,
	bool adminUiEnabled) : IDisposable
{
	public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private const string ChallengeCookieName = "eventstore-ui-oauth-pkce";
	private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
	private readonly IDataProtector _challengeProtector = dataProtectionProvider.CreateProtector("EventStore.ClusterNode.Components.Services.OAuthBrowserFlowService.Pkce");

	public OAuthCodeChallenge CreateCodeChallenge(HttpContext context)
	{
		var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
		var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
		var correlationId = Base64Url(RandomNumberGenerator.GetBytes(32));
		var payload = new OAuthChallengeCookie(verifier, correlationId, timeProvider.GetUtcNow().Add(ChallengeLifetime));

		context.Response.Cookies.Append(
			ChallengeCookieName,
			_challengeProtector.Protect(JsonSerializer.Serialize(payload, JsonOptions)),
			ChallengeCookieOptions(context.Request));
		return new OAuthCodeChallenge(correlationId, challenge, "S256");
	}

	public async Task<IResult> HandleCallback(HttpContext context, CancellationToken cancellationToken)
	{
		var code = context.Request.Query["code"].ToString();
		var state = context.Request.Query["state"].ToString();
		var providerError = context.Request.Query["error"].ToString();
		var hasState = TryReadState(state, out var correlationId, out var returnUrl, out var redirectUri);
		if (!string.IsNullOrWhiteSpace(providerError))
		{
			DeleteChallengeCookie(context);
			return ErrorRedirect("provider_error", hasState ? returnUrl : "");
		}

		if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
		{
			DeleteChallengeCookie(context);
			return ErrorRedirect("missing_callback", "");
		}

		if (!hasState)
		{
			DeleteChallengeCookie(context);
			return ErrorRedirect("invalid_state", "");
		}

		if (!TryReadChallenge(context.Request, correlationId, out var challenge) ||
			challenge.ExpiresAt <= timeProvider.GetUtcNow())
		{
			DeleteChallengeCookie(context);
			return ErrorRedirect("invalid_state", returnUrl);
		}

		DeleteChallengeCookie(context);
		var token = await ExchangeCode(
			code,
			challenge.CodeVerifier,
			RedirectUri(context.Request, redirectUri),
			cancellationToken);
		if (string.IsNullOrWhiteSpace(token))
		{
			return ErrorRedirect("missing_token", returnUrl);
		}

		if (!LooksLikeJwt(token))
		{
			return ErrorRedirect("unsupported_token", returnUrl);
		}

		var validationResult = await tokenValidator.ValidateTokenAsync(token, cancellationToken);
		if (!validationResult.IsValid)
		{
			return ErrorRedirect("invalid_token", returnUrl);
		}

		UiCredentialCookie.Delete(context.Response);
		UiCredentialCookie.AppendOAuthToken(context.Response, token);
		return Results.Redirect(adminUiEnabled ? SignInLocation(returnUrl) : DirectReturnLocation(returnUrl));
	}

	private async Task<string> ExchangeCode(
		string code,
		string codeVerifier,
		string baseUri,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(options.ClientId))
		{
			return "";
		}

		if (string.IsNullOrWhiteSpace(options.TokenEndpoint))
		{
			return "";
		}

		var values = new Dictionary<string, string>
		{
			["grant_type"] = "authorization_code",
			["client_id"] = options.ClientId,
			["code"] = code,
			["redirect_uri"] = baseUri,
			["code_verifier"] = codeVerifier
		};

		if (!string.IsNullOrWhiteSpace(options.ClientSecret))
		{
			values["client_secret"] = options.ClientSecret;
		}

		using var response = await httpClient.PostAsync(
			TokenEndpoint(options),
			new FormUrlEncodedContent(values),
			cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			return "";
		}

		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
		var tokenResponse = await JsonSerializer.DeserializeAsync<OAuthTokenResponse>(stream, JsonOptions, cancellationToken);
		return tokenResponse?.AccessToken ?? "";
	}

	private static bool LooksLikeJwt(string token)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return false;
		}

		var segments = token.Split('.');
		return segments.Length is 3 or 5 && segments.All(segment => !string.IsNullOrWhiteSpace(segment));
	}

	private bool TryReadChallenge(HttpRequest request, string correlationId, out OAuthChallengeCookie challenge)
	{
		challenge = new OAuthChallengeCookie("", "", DateTimeOffset.MinValue);
		try
		{
			if (!request.Cookies.TryGetValue(ChallengeCookieName, out var value))
			{
				return false;
			}

			var json = _challengeProtector.Unprotect(value);
			challenge = JsonSerializer.Deserialize<OAuthChallengeCookie>(json, JsonOptions) ?? challenge;
			return !string.IsNullOrWhiteSpace(challenge.CodeVerifier) &&
				string.Equals(challenge.CorrelationId, correlationId, StringComparison.Ordinal);
		}
		catch (Exception ex) when (ex is CryptographicException or JsonException)
		{
			return false;
		}
	}

	private IResult ErrorRedirect(string error, string returnUrl)
	{
		var location = adminUiEnabled ? SignInLocation(returnUrl) : DirectReturnLocation(returnUrl);
		return Results.Redirect($"{location}{(location.Contains('?') ? '&' : '?')}oauth_error={Uri.EscapeDataString(error)}");
	}

	private bool TryReadState(string state, out string correlationId, out string returnUrl, out string redirectUri)
	{
		correlationId = "";
		returnUrl = "";
		redirectUri = "";
		try
		{
			var json = Encoding.UTF8.GetString(Convert.FromBase64String(state));
			using var document = JsonDocument.Parse(json);
			if (!document.RootElement.TryGetProperty("code_challenge_correlation_id", out var element))
			{
				return false;
			}

			correlationId = element.GetString() ?? "";
			if (document.RootElement.TryGetProperty("return_url", out var returnUrlElement))
			{
				returnUrl = SecurityBrowserService.NormalizeReturnUrl(returnUrlElement.GetString() ?? "");
			}

			if (document.RootElement.TryGetProperty("redirect_uri", out var redirectUriElement))
			{
				redirectUri = NormalizeRedirectUri(redirectUriElement.GetString() ?? "");
			}

			return !string.IsNullOrWhiteSpace(correlationId);
		}
		catch (Exception ex) when (ex is FormatException or JsonException)
		{
			return false;
		}
	}

	private string RedirectUri(HttpRequest request, string redirectUri) =>
		string.IsNullOrWhiteSpace(redirectUri)
			? $"{request.Scheme}://{request.Host}{options.RedirectPath}"
			: redirectUri;

	private string NormalizeRedirectUri(string redirectUri)
	{
		if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
		{
			return "";
		}

		if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
			!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
		{
			return "";
		}

		return uri.AbsolutePath.Equals(options.RedirectPath, StringComparison.Ordinal)
			? uri.GetLeftPart(UriPartial.Path)
			: "";
	}

	private static string SignInLocation(string returnUrl) =>
		string.IsNullOrWhiteSpace(returnUrl) || returnUrl == "/ui"
			? "/ui/signin"
			: $"/ui/signin?returnUrl={Uri.EscapeDataString(returnUrl)}";

	private static string DirectReturnLocation(string returnUrl) =>
		string.IsNullOrWhiteSpace(returnUrl) ||
		returnUrl.Equals("/ui", StringComparison.OrdinalIgnoreCase) ||
		returnUrl.StartsWith("/ui/", StringComparison.OrdinalIgnoreCase)
			? "/"
			: returnUrl;

	private void DeleteChallengeCookie(HttpContext context) =>
		context.Response.Cookies.Delete(ChallengeCookieName, ChallengeCookieOptions(context.Request, maxAge: null));

	private CookieOptions ChallengeCookieOptions(HttpRequest request) =>
		ChallengeCookieOptions(request, ChallengeLifetime);

	private CookieOptions ChallengeCookieOptions(HttpRequest request, TimeSpan? maxAge) => new()
	{
		HttpOnly = true,
		Secure = request.IsHttps,
		SameSite = SameSiteMode.Lax,
		Path = "/",
		MaxAge = maxAge
	};

	private static string TokenEndpoint(ClusterVNodeOptions.OAuthOptions options) =>
		options.TokenEndpoint;

	private static string Base64Url(byte[] bytes) =>
		Convert.ToBase64String(bytes)
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');

	public void Dispose() =>
		httpClient.Dispose();

	private sealed record OAuthChallengeCookie(string CodeVerifier, string CorrelationId, DateTimeOffset ExpiresAt);
}

public sealed record OAuthCodeChallenge(
	[property: JsonPropertyName("code_challenge_correlation_id")]
	string CodeChallengeCorrelationId,
	[property: JsonPropertyName("code_challenge")]
	string CodeChallenge,
	[property: JsonPropertyName("code_challenge_method")]
	string CodeChallengeMethod);

public sealed record OAuthTokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
