using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.LogCommon;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.DeletingStream;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint), Ignore = "No such thing as a V0 prepare in LogV3")]
public class WhenHardDeletingStreamWithLogVersion0<TLogFormat, TStreamId> : ReadIndexTestScenario<TLogFormat, TStreamId>
{
	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		await WriteSingleEvent("ES1", 0, new string('.', 3000), token: token);
		await WriteSingleEvent("ES1", 1, new string('.', 3000), token: token);

		await WriteV0HardDelete("ES1", token);
	}

	private async ValueTask WriteV0HardDelete(string eventStreamId, CancellationToken token)
	{
		var logPosition = Writer.Position;
		var prepare = new PrepareLogRecord(logPosition, Guid.NewGuid(), Guid.NewGuid(), logPosition, 0,
			eventStreamId, null,
			int.MaxValue - 1, DateTime.UtcNow,
			PrepareFlags.StreamDelete | PrepareFlags.TransactionBegin | PrepareFlags.TransactionEnd,
			SystemEventTypes.StreamDeleted, null, Array.Empty<byte>(), Array.Empty<byte>(),
			prepareRecordVersion: LogRecordVersion.LogRecordV0);
		var (_, pos) = await Writer.Write(prepare, token);

		var commit = new CommitLogRecord(pos, prepare.CorrelationId,
			prepare.LogPosition, DateTime.UtcNow, int.MaxValue,
			commitRecordVersion: LogRecordVersion.LogRecordV0);
		await Writer.Write(commit, token);
	}

	[Test]
	public async Task should_change_expected_version_to_deleted_event_number_when_reading()
	{
		var chunk = Db.Manager.GetChunk(0);
		var chunkRecords = new List<ILogRecord>();
		RecordReadResult result = await chunk.TryReadFirst(CancellationToken.None);
		while (result.Success)
		{
			chunkRecords.Add(result.LogRecord);
			result = chunk.TryReadClosestForward(result.NextPosition);
		}

		Assert.That(chunkRecords.Any(x =>
			x.RecordType == LogRecordType.Commit && ((CommitLogRecord)x).FirstEventNumber == long.MaxValue));
		Assert.That(chunkRecords.Any(x =>
			x.RecordType == LogRecordType.Prepare && ((PrepareLogRecord)x).ExpectedVersion == long.MaxValue - 1));
	}
}
