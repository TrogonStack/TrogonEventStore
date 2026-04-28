using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed record UiCredentials(string Username, string Password) {
	public string BasicValue => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
}

public static class UiCredentialCookie {
	public const string BasicCookieName = "es-creds";
	public const string OAuthCookieName = "oauth_id_token";

	public static void AppendBasic(HttpResponse response, UiCredentials credentials) =>
		response.Cookies.Append(
			BasicCookieName,
			JsonSerializer.Serialize(new { credentials = credentials.BasicValue }),
			Options(response.HttpContext.Request.IsHttps));

	public static void Delete(HttpResponse response) =>
		response.Cookies.Delete(BasicCookieName, Options(response.HttpContext.Request.IsHttps));

	public static void DeleteOAuthToken(HttpResponse response) =>
		response.Cookies.Delete(OAuthCookieName, Options(response.HttpContext.Request.IsHttps));

	public static bool TryReadAuthorization(HttpRequest request, out string scheme, out string value) {
		if (TryReadBearer(request, out value)) {
			scheme = "Bearer";
			return true;
		}

		if (TryReadBasic(request, out value)) {
			scheme = "Basic";
			return true;
		}

		scheme = "";
		value = "";
		return false;
	}

	private static bool TryReadBearer(HttpRequest request, out string value) {
		value = "";
		if (!request.Cookies.TryGetValue(OAuthCookieName, out var token) || string.IsNullOrWhiteSpace(token))
			return false;

		value = SafeDecode(token);
		return IsHeaderSafe(value);
	}

	private static bool TryReadBasic(HttpRequest request, out string value) {
		value = "";
		if (!request.Cookies.TryGetValue(BasicCookieName, out var raw) || string.IsNullOrWhiteSpace(raw))
			return false;

		if (!TryExtractBasicValue(SafeDecode(raw), out value))
			return false;

		return IsHeaderSafe(value) && IsValidBasicValue(value);
	}

	private static bool TryExtractBasicValue(string raw, out string value) {
		value = "";
		var candidate = raw.StartsWith("j:", StringComparison.Ordinal) ? raw[2..] : raw;
		if (!candidate.StartsWith("{", StringComparison.Ordinal)) {
			value = candidate;
			return true;
		}

		try {
			using var document = JsonDocument.Parse(candidate);
			if (!document.RootElement.TryGetProperty("credentials", out var credentials) &&
			    !document.RootElement.TryGetProperty("Credentials", out credentials))
				return false;

			value = credentials.GetString() ?? "";
			return !string.IsNullOrWhiteSpace(value);
		} catch (JsonException) {
			return false;
		}
	}

	private static bool IsValidBasicValue(string value) {
		try {
			var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
			return decoded.Contains(':', StringComparison.Ordinal);
		} catch (FormatException) {
			return false;
		}
	}

	private static bool IsHeaderSafe(string value) =>
		!value.Any(x => x is '\r' or '\n');

	private static string SafeDecode(string value) {
		try {
			return Uri.UnescapeDataString(value);
		} catch (UriFormatException) {
			return value;
		}
	}

	private static CookieOptions Options(bool secure) => new() {
		HttpOnly = true,
		IsEssential = true,
		Path = "/",
		SameSite = SameSiteMode.Lax,
		Secure = secure
	};
}
