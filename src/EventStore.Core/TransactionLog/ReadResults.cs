using EventStore.Core.TransactionLog.LogRecords;
using EventStore.LogCommon;

namespace EventStore.Core.TransactionLog;

public readonly struct RecordReadResult(bool success, long nextPosition, ILogRecord logRecord, int recordLength)
{
	public static readonly RecordReadResult Failure = new(false, -1, null, 0);

	public readonly bool Success = success;
	public readonly long NextPosition = nextPosition;
	public readonly ILogRecord LogRecord = logRecord;
	public readonly int RecordLength = recordLength;

	public override string ToString() =>
		$"Success: {Success}, NextPosition: {NextPosition}, RecordLength: {RecordLength}, LogRecord: {LogRecord}";
}

public readonly struct RawReadResult(bool success, long nextPosition, byte[] record, int recordLength)
{
	public static readonly RawReadResult Failure = new RawReadResult(false, -1, null, 0);

	public readonly bool Success = success;
	public readonly long NextPosition = nextPosition;
	public readonly byte[] RecordBuffer = record; // can be longer than the record
	public readonly int RecordLength = recordLength;

	public LogRecordType RecordType => (LogRecordType)RecordBuffer[0];

	public override string ToString() =>
		$"Success: {Success}, NextPosition: {NextPosition}, Record Length: {RecordLength}";
}

public readonly struct SeqReadResult(
	bool success,
	bool eof,
	ILogRecord logRecord,
	int recordLength,
	long recordPrePosition,
	long recordPostPosition)
{
	public static readonly SeqReadResult Failure = new SeqReadResult(false, true, null, 0, -1, -1);

	public readonly bool Success = success;
	public readonly bool Eof = eof;
	public readonly ILogRecord LogRecord = logRecord;
	public readonly int RecordLength = recordLength;
	public readonly long RecordPrePosition = recordPrePosition;
	public readonly long RecordPostPosition = recordPostPosition;

	public override string ToString() =>
		$"Success: {Success}, RecordLength: {RecordLength}, RecordPrePosition: {RecordPrePosition}, RecordPostPosition: {RecordPostPosition}, LogRecord: {LogRecord}";
}
