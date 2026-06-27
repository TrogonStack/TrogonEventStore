using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Services.Storage.InMemory;

namespace EventStore.Core.Services.Storage.Indexing;

public interface IIndexingComponent : IAsyncDisposable, IVirtualStreamReaderProvider
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
