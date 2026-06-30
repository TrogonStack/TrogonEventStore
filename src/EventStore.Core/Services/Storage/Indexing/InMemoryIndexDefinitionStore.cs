using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed class InMemoryIndexDefinitionStore : IIndexDefinitionStore
{
	private readonly object _lock = new();
	private readonly Dictionary<string, StoredIndexDefinition> _definitions = new(StringComparer.Ordinal);

	public ValueTask<IndexDefinitionCreateResult> Create(IndexName name, IndexDefinition definition, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(definition);
		token.ThrowIfCancellationRequested();

		lock (_lock)
		{
			if (!_definitions.TryGetValue(name.Value, out var existing))
			{
				_definitions.Add(name.Value, new StoredIndexDefinition(name, definition));
				return ValueTask.FromResult(IndexDefinitionCreateResult.Created);
			}

			return ValueTask.FromResult(existing.Definition == definition
				? IndexDefinitionCreateResult.AlreadyExists
				: IndexDefinitionCreateResult.Conflicts);
		}
	}

	public ValueTask<StoredIndexDefinition> Read(IndexName name, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(name);
		token.ThrowIfCancellationRequested();

		lock (_lock)
		{
			_definitions.TryGetValue(name.Value, out var definition);
			return ValueTask.FromResult(definition);
		}
	}

	public ValueTask<IReadOnlyList<StoredIndexDefinition>> List(CancellationToken token)
	{
		token.ThrowIfCancellationRequested();

		lock (_lock)
		{
			return ValueTask.FromResult<IReadOnlyList<StoredIndexDefinition>>(
				_definitions.Values.OrderBy(static definition => definition.Name.Value, StringComparer.Ordinal).ToArray());
		}
	}

	public ValueTask<bool> Delete(IndexName name, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(name);
		token.ThrowIfCancellationRequested();

		lock (_lock)
		{
			return ValueTask.FromResult(_definitions.Remove(name.Value));
		}
	}
}
