using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.LogRecords;

namespace EventStore.Core.Tests.Fakes;

public class FakeIndexReader(Func<long, bool> existsAt = null) : ITransactionFileReader
{
	private readonly Func<long, bool> _existsAt = existsAt ?? (l => true);

	public void Reposition(long position) => throw new NotImplementedException();

	public SeqReadResult TryReadNext() => throw new NotImplementedException();

	public ValueTask<SeqReadResult> TryReadPrev(CancellationToken token)
		=> ValueTask.FromException<SeqReadResult>(new NotImplementedException());

	public RecordReadResult TryReadAt(long position, bool couldBeScavenged)
	{
		var record = (LogRecord)new PrepareLogRecord(position, Guid.NewGuid(), Guid.NewGuid(), 0, 0,
			position.ToString(), null, -1, DateTime.UtcNow, PrepareFlags.None, "type", null, Array.Empty<byte>(), null);
		return new RecordReadResult(true, position + 1, record, 1);
	}

	public bool ExistsAt(long position) => _existsAt(position);
}
