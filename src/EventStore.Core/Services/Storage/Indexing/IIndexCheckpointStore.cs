using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.Services.Storage.Indexing;

public interface IIndexCheckpointStore
{
	ValueTask<IndexCheckpoint?> Read(CancellationToken token);

	ValueTask Write(IndexCheckpoint checkpoint, CancellationToken token);
}
