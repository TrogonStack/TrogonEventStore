using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.LogRecords;

namespace EventStore.Core.TransactionLog {
	public interface ITransactionFileWriter : IDisposable {
		void Open();
		bool CanWrite(int numBytes);
		ValueTask<(bool, long)> Write(ILogRecord record, CancellationToken token);
		void OpenTransaction();
		void WriteToTransaction(ILogRecord record, out long newPos);
		bool TryWriteToTransaction(ILogRecord record, out long newPos);
		void CommitTransaction();
		bool HasOpenTransaction();
		void Flush();
		void Close();

		long Position { get; }
		long FlushedPosition { get; }
	}
}
