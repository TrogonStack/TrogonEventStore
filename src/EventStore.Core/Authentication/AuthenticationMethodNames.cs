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
		if (!IsLegacyInternal(options.AuthenticationType))
		{
			return [Normalize(options.AuthenticationType)];
		}

		var methods = options.Methods
			.Where(method => !string.IsNullOrWhiteSpace(method))
			.Select(Normalize)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		return methods.Length == 0 ? [Password] : methods;
	}

	public static bool IncludesPassword(ClusterVNodeOptions.AuthOptions options) =>
		FromOptions(options).Any(IsPassword);

	public static string Normalize(string method) =>
		IsLegacyInternal(method) ? Password : method.Trim().ToLowerInvariant();

	public static bool IsPassword(string method) =>
		string.Equals(Normalize(method), Password, StringComparison.OrdinalIgnoreCase);

	private static bool IsLegacyInternal(string method) =>
		string.Equals(method?.Trim(), LegacyInternal, StringComparison.OrdinalIgnoreCase);
}
