using System;
using System.IO;
using DotNext.Buffers;
using DotNext.IO;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.LogCommon;
using Xunit;

namespace EventStore.Core.XUnit.Tests.TransactionLog.LogRecords;

public class PrepareLogRecordTests
{
	[Theory]
	[InlineData(LogRecordVersion.LogRecordV0)]
	[InlineData(LogRecordVersion.LogRecordV1)]
	public void copy_for_retry_preserves_record_version(byte version)
	{
		var record = new PrepareLogRecord(
			logPosition: 100,
			correlationId: Guid.NewGuid(),
			eventId: Guid.NewGuid(),
			transactionPosition: 100,
			transactionOffset: 0,
			eventStreamId: "stream",
			eventStreamIdSize: null,
			expectedVersion: 0,
			timeStamp: DateTime.UtcNow,
			flags: PrepareFlags.SingleWrite,
			eventType: "event-type",
			eventTypeSize: null,
			data: new byte[] { 1, 2, 3 },
			metadata: new byte[] { 4, 5, 6 },
			prepareRecordVersion: version);

		var retry = record.CopyForRetry(logPosition: 200, transactionPosition: 200);

		Assert.Equal(version, retry.Version);

		var writer = new BufferWriterSlim<byte>();
		retry.WriteTo(ref writer);

		using var recordBuffer = writer.DetachOrCopyBuffer();
		var reader = new SequenceReader(new(recordBuffer.Memory));
		var parsed = Assert.IsType<PrepareLogRecord>(LogRecord.ReadFrom(ref reader));

		Assert.Equal(version, parsed.Version);
	}
}
