using System;

namespace EventStore.Core.TransactionLog.Chunks.TFChunk;

public class PrefixingLocatorCodec : ILocatorCodec
{
	private const string RemotePrefix = "archived-chunk-";

	public string EncodeRemote(int chunkNumber) => $"{RemotePrefix}{chunkNumber}";

	public bool Decode(string locator, out int chunkNumber, out string fileName)
	{
		if (locator.StartsWith(RemotePrefix, StringComparison.Ordinal))
		{
			chunkNumber = int.Parse(locator.AsSpan(RemotePrefix.Length));
			fileName = string.Empty;
			return true;
		}

		chunkNumber = default;
		fileName = locator;
		return false;
	}
}
