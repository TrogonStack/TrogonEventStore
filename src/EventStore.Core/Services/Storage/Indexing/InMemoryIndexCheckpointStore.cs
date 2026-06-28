using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.Services.Storage.Indexing;

public sealed class InMemoryIndexCheckpointStore : IIndexCheckpointStore
{
	private readonly object _lock = new();
	private IndexCheckpoint? _checkpoint;

	public ValueTask<IndexCheckpoint?> Read(CancellationToken token)
	{
		token.ThrowIfCancellationRequested();

		lock (_lock)
		{
			return ValueTask.FromResult(_checkpoint);
		}
	}

	public ValueTask Write(IndexCheckpoint checkpoint, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();

		lock (_lock)
		{
			_checkpoint = checkpoint;
		}

		return ValueTask.CompletedTask;
	}
}
