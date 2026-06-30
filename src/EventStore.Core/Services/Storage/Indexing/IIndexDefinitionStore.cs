using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace EventStore.Core.Services.Storage.Indexing;

public interface IIndexDefinitionStore
{
	ValueTask<IndexDefinitionCreateResult> Create(IndexName name, IndexDefinition definition, CancellationToken token);

	ValueTask<StoredIndexDefinition?> Read(IndexName name, CancellationToken token);

	ValueTask<IReadOnlyList<StoredIndexDefinition>> List(CancellationToken token);

	ValueTask<bool> Delete(IndexName name, CancellationToken token);
}

public enum IndexDefinitionCreateResult
{
	Created,
	AlreadyExists,
	Conflicts
}

public sealed record StoredIndexDefinition
{
	public IndexName Name { get; }

	public IndexDefinition Definition { get; }

	public StoredIndexDefinition(IndexName name, IndexDefinition definition)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
		Definition = definition ?? throw new ArgumentNullException(nameof(definition));
	}
}
