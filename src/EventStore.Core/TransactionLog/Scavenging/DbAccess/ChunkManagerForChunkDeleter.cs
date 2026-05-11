using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks;

namespace EventStore.Core.TransactionLog.Scavenging;

public class ChunkManagerForChunkDeleter(TFChunkManager manager) : IChunkManagerForChunkDeleter
{
	public ValueTask<bool> SwitchInChunks(IReadOnlyList<string> locators, CancellationToken token) =>
		manager.SwitchInCompletedChunks(locators, token);
}
