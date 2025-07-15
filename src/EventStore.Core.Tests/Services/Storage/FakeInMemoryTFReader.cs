using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.LogRecords;

namespace EventStore.Core.Tests.Services.Storage;

public class FakeInMemoryTfReader(int recordOffset) : ITransactionFileReader
{
	private Dictionary<long, ILogRecord> _records = new();
	private long _curPosition;

	public int NumReads { get; private set; }

	public void AddRecord(ILogRecord record, long position) => _records.Add(position, record);

	public void Reposition(long position) => _curPosition = position;

	public ValueTask<SeqReadResult> TryReadNext(CancellationToken token)
	{
		NumReads++;

		SeqReadResult result;
		if (_records.ContainsKey(_curPosition))
		{
			var pos = _curPosition;
			_curPosition += recordOffset;
			result = new SeqReadResult(true, false, _records[pos], recordOffset, pos, pos + recordOffset);
		}
		else
		{
			result = new SeqReadResult(false, false, null, 0, 0, 0);
		}

		return new ValueTask<SeqReadResult>(result);
	}

	public ValueTask<SeqReadResult> TryReadPrev(CancellationToken token)
		=> ValueTask.FromException<SeqReadResult>(new NotImplementedException());

	public ValueTask<RecordReadResult> TryReadAt(long position, bool couldBeScavenged, CancellationToken token)
	{
		NumReads++;

		RecordReadResult result;
		if (_records.ContainsKey(position))
		{
			result = new RecordReadResult(true, 0, _records[position], 0);
		}
		else
		{
			result = new RecordReadResult(false, 0, _records[position], 0);
		}

		return new(result);
	}

	public ValueTask<bool> ExistsAt(long position, CancellationToken token)
		=> token.IsCancellationRequested
			? ValueTask.FromCanceled<bool>(token)
			: ValueTask.FromResult(_records.ContainsKey(position));
}
