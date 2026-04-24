using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.Scavenging;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class TracingChunkManagerForChunkDeleter(
	HashSet<int> remoteChunks,
	ILocatorCodec locatorCodec) : IChunkManagerForChunkDeleter
{
	public ValueTask<bool> SwitchInChunks(IReadOnlyList<string> locators, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();

		foreach (var locator in locators)
		{
			if (!locatorCodec.Decode(locator, out var chunkNumber, out _))
				return ValueTask.FromResult(false);

			remoteChunks.Add(chunkNumber);
		}

		return ValueTask.FromResult(true);
	}
}
