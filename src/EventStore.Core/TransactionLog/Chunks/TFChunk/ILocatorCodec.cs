namespace EventStore.Core.TransactionLog.Chunks.TFChunk;

public interface ILocatorCodec
{
	string EncodeRemote(int chunkNumber);

	bool Decode(string locator, out int chunkNumber, out string fileName);
}
