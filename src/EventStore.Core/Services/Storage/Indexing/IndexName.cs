using System;
using System.Linq;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed record IndexName
{
	public string Value { get; }

	public IndexName(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new ArgumentException("Index name cannot be empty.", nameof(value));
		}

		if (!value.All(IsValidCharacter))
		{
			throw new ArgumentException("Index name can contain only lowercase alphanumeric characters, underscores and dashes.", nameof(value));
		}

		Value = value;
	}

	public override string ToString() => Value;

	private static bool IsValidCharacter(char value) =>
		value is >= 'a' and <= 'z'
			or >= '0' and <= '9'
			or '_'
			or '-';
}
