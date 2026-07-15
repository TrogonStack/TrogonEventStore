using System;
using System.Collections.Generic;
using System.Linq;

namespace EventStore.Core.Authentication;

public static class AuthenticationMethodNames
{
	public const string LegacyInternal = "internal";
	public const string Password = "password";
	public const string OAuth = "oauth";

	public static IReadOnlyList<string> FromOptions(ClusterVNodeOptions.AuthOptions options)
	{
		var methods = (options.Methods ?? [])
			.Where(method => !string.IsNullOrWhiteSpace(method))
			.Select(Normalize)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		if (methods.Length > 0)
		{
			return methods;
		}

		return IsLegacyInternal(options.AuthenticationType) ? [Password] : [Normalize(options.AuthenticationType)];
	}

	public static bool IncludesPassword(ClusterVNodeOptions.AuthOptions options) =>
		FromOptions(options).Any(IsPassword);

	public static bool IncludesOAuth(ClusterVNodeOptions.AuthOptions options) =>
		FromOptions(options).Any(method => string.Equals(Normalize(method), OAuth, StringComparison.OrdinalIgnoreCase));

	public static string Normalize(string method) =>
		IsLegacyInternal(method) ? Password : method?.Trim().ToLowerInvariant() ?? string.Empty;

	public static bool IsPassword(string method) =>
		string.Equals(Normalize(method), Password, StringComparison.OrdinalIgnoreCase);

	private static bool IsLegacyInternal(string method) =>
		string.Equals(method?.Trim(), LegacyInternal, StringComparison.OrdinalIgnoreCase);
}
