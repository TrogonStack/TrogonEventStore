using EventStore.Core.Time;
using EventStore.Core.TransactionLog.LogRecords;

namespace EventStore.Core.TransactionLog;

public interface ITransactionFileTracker {
	void OnRead(Instant start, ILogRecord record, Source source);

	public enum Source {
		Unknown,
		Archive,
		ChunkCache,
		FileSystem,
	}
}
