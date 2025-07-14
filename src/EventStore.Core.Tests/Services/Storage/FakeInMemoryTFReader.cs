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

	public SeqReadResult TryReadNext()
	{
		NumReads++;
		if (!_records.ContainsKey(_curPosition))
			return new SeqReadResult(false, false, null, 0, 0, 0);

		var pos = _curPosition;
		_curPosition += recordOffset;
		return new SeqReadResult(true, false, _records[pos], recordOffset, pos, pos + recordOffset);
	}

	public ValueTask<SeqReadResult> TryReadPrev(CancellationToken token)
		=> ValueTask.FromException<SeqReadResult>(new NotImplementedException());

	public RecordReadResult TryReadAt(long position, bool couldBeScavenged)
	{
		NumReads++;
		return _records.TryGetValue(position, out var record) ?
			new RecordReadResult(true, 0, record, 0) :
			new RecordReadResult(false, 0, _records[position], 0);
	}

	public bool ExistsAt(long position) => _records.ContainsKey(position);
}
