using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EventStore.ClusterNode.Components.Services;

internal static class OAuthBrowserFlowEndpoints
{
	public static IEndpointRouteBuilder MapOAuthBrowserFlowEndpoints(
		this IEndpointRouteBuilder app,
		ClusterVNodeOptions.OAuthOptions options)
	{
		app.MapGet(options.CodeChallengePath, (OAuthBrowserFlowService service) =>
			Results.Json(service.CreateCodeChallenge(), OAuthBrowserFlowService.JsonOptions));

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
	TimeProvider timeProvider) : IDisposable
{
	public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly ConcurrentDictionary<string, PendingChallenge> _pendingChallenges = new();

	public OAuthCodeChallenge CreateCodeChallenge()
	{
		PruneExpired();

		var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
		var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
		var correlationId = Base64Url(RandomNumberGenerator.GetBytes(32));

		_pendingChallenges[correlationId] = new PendingChallenge(verifier, timeProvider.GetUtcNow().AddMinutes(5));
		return new OAuthCodeChallenge(correlationId, challenge, "S256");
	}

	public async Task<IResult> HandleCallback(HttpContext context, CancellationToken cancellationToken)
	{
		var code = context.Request.Query["code"].ToString();
		var state = context.Request.Query["state"].ToString();
		if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
		{
			return SignInRedirect("missing_callback");
		}

		if (!TryReadCorrelationId(state, out var correlationId) ||
			!_pendingChallenges.TryRemove(correlationId, out var pending) ||
			pending.ExpiresAt <= timeProvider.GetUtcNow())
		{
			return SignInRedirect("invalid_state");
		}

		var token = await ExchangeCode(code, pending.CodeVerifier, PublicBaseUri(context.Request), cancellationToken);
		if (string.IsNullOrWhiteSpace(token))
		{
			return SignInRedirect("missing_token");
		}

		UiCredentialCookie.Delete(context.Response);
		UiCredentialCookie.AppendOAuthToken(context.Response, token);
		return Results.Redirect("/ui/signin");
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
			["redirect_uri"] = $"{baseUri}{options.RedirectPath}",
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

	private void PruneExpired()
	{
		var now = timeProvider.GetUtcNow();
		foreach (var (key, value) in _pendingChallenges)
		{
			if (value.ExpiresAt <= now)
			{
				_pendingChallenges.TryRemove(key, out _);
			}
		}
	}

	private static IResult SignInRedirect(string error) =>
		Results.Redirect($"/ui/signin?oauth_error={Uri.EscapeDataString(error)}");

	private static bool TryReadCorrelationId(string state, out string correlationId)
	{
		correlationId = "";
		try
		{
			var json = Encoding.UTF8.GetString(Convert.FromBase64String(state));
			using var document = JsonDocument.Parse(json);
			if (!document.RootElement.TryGetProperty("code_challenge_correlation_id", out var element))
			{
				return false;
			}

			correlationId = element.GetString() ?? "";
			return !string.IsNullOrWhiteSpace(correlationId);
		}
		catch (Exception ex) when (ex is FormatException or JsonException)
		{
			return false;
		}
	}

	private static string PublicBaseUri(HttpRequest request) =>
		$"{request.Scheme}://{request.Host}";

	private static string TokenEndpoint(ClusterVNodeOptions.OAuthOptions options) =>
		options.TokenEndpoint;

	private static string Base64Url(byte[] bytes) =>
		Convert.ToBase64String(bytes)
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');

	public void Dispose() =>
		httpClient.Dispose();

	private sealed record PendingChallenge(string CodeVerifier, DateTimeOffset ExpiresAt);
}

public sealed record OAuthCodeChallenge(
	[property: JsonPropertyName("code_challenge_correlation_id")]
	string CodeChallengeCorrelationId,
	[property: JsonPropertyName("code_challenge")]
	string CodeChallenge,
	[property: JsonPropertyName("code_challenge_method")]
	string CodeChallengeMethod);

public sealed record OAuthTokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
