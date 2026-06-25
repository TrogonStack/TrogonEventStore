using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;

namespace EventStore.Core.Services.Storage.Indexing;

public interface IIndexingComponent : IAsyncDisposable
{
	ValueTask Initialize(CancellationToken token);

	ValueTask<IndexCheckpoint?> ReadCheckpoint(CancellationToken token);

	IIndexingProcessor Processor { get; }
}

public interface IIndexingProcessor
{
	ValueTask Index(ResolvedEvent resolvedEvent, CancellationToken token);

	ValueTask Commit(CancellationToken token);
}
