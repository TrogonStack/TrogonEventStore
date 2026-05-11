using System.Collections.Generic;
using System.Threading;

namespace EventStore.Core.TransactionLog.Chunks;

public interface IChunkEnumerator
{
	IAsyncEnumerable<TFChunkInfo> EnumerateChunks(int lastChunkNumber, CancellationToken token);
}
