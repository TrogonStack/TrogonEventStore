using System;
using System.Collections.Generic;
using System.Linq;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed record IndexDefinition
{
	public IndexEventFilter Filter { get; }

	public IReadOnlyList<IndexFieldDefinition> Fields { get; }

	public IndexDefinition(IndexEventFilter filter, IReadOnlyList<IndexFieldDefinition> fields)
	{
		ArgumentNullException.ThrowIfNull(fields);

		if (fields.Any(static field => field is null))
		{
			throw new ArgumentException("Index fields cannot contain null.", nameof(fields));
		}

		if (filter is null && fields.Count == 0)
		{
			throw new ArgumentException("Index definition must specify at least one filter or field.", nameof(fields));
		}

		Filter = filter;
		Fields = fields.ToArray();
	}

	public bool Equals(IndexDefinition other) =>
		other is not null
		&& Equals(Filter, other.Filter)
		&& Fields.SequenceEqual(other.Fields);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(Filter);

		foreach (var field in Fields)
		{
			hash.Add(field);
		}

		return hash.ToHashCode();
	}
}

public sealed record IndexEventFilter
{
	public string Value { get; }

	public IndexEventFilter(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new ArgumentException("Index filter cannot be empty.", nameof(value));
		}

		Value = value;
	}

	public override string ToString() => Value;
}

public sealed record IndexFieldDefinition
{
	public string Name { get; }

	public IndexFieldSelector Selector { get; }

	public IndexFieldDefinition(string name, IndexFieldSelector selector)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("Index field name cannot be empty.", nameof(name));
		}

		ArgumentNullException.ThrowIfNull(selector);

		Name = name;
		Selector = selector;
	}
}

public sealed record IndexFieldSelector
{
	public string Value { get; }

	public IndexFieldSelector(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new ArgumentException("Index field selector cannot be empty.", nameof(value));
		}

		Value = value;
	}

	public override string ToString() => Value;
}
