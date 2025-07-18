using System;
using System.Collections.Generic;
using System.Linq;

namespace EventStore.Common.Utils;

public static class StringExtensions
{
	public static bool IsEmptyString(this string s)
	{
		return string.IsNullOrEmpty(s);
	}

	public static bool IsNotEmptyString(this string s)
	{
		return !string.IsNullOrEmpty(s);
	}

	public static bool EqualsOrdinalIgnoreCase(this string a, string b) =>
		string.Compare(a, b, StringComparison.OrdinalIgnoreCase) == 0;

	public static bool EqualsOrdinalIgnoreCase(this IEnumerable<string> a, IEnumerable<string> b) =>
		a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
}
