using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Tests.TransactionLog.Scavenging.Helpers;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog.Scavenging;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class when_having_stream_with_strict_max_age_leaving_no_events_in_stream<TLogFormat, TStreamId> : ScavengeTestScenario<TLogFormat, TStreamId>
{
	protected override ValueTask<DbResult> CreateDb(TFChunkDbCreationHelper<TLogFormat, TStreamId> dbCreator, CancellationToken token)
	{
		return dbCreator
			.Chunk(
				Rec.Prepare(0, "$$bla",
					metadata: new StreamMetadata(null, TimeSpan.FromMinutes(1), null, null, null)),
				Rec.Commit(0, "$$bla"),
				Rec.Prepare(1, "bla", timestamp: DateTime.UtcNow - TimeSpan.FromMinutes(25)),
				Rec.Commit(1, "bla"),
				Rec.Prepare(2, "bla", timestamp: DateTime.UtcNow - TimeSpan.FromMinutes(20)),
				Rec.Prepare(2, "bla", timestamp: DateTime.UtcNow - TimeSpan.FromMinutes(10)),
				Rec.Prepare(2, "bla", timestamp: DateTime.UtcNow - TimeSpan.FromMinutes(5)),
				Rec.Prepare(2, "bla", timestamp: DateTime.UtcNow - TimeSpan.FromMinutes(3)),
				Rec.Commit(2, "bla"))
			.CompleteLastChunk()
			.CreateDb(token: token);
	}

	protected override ILogRecord[][] KeptRecords(DbResult dbResult)
	{
		var keep = LogFormatHelper<TLogFormat, TStreamId>.IsV2
			? new int[] { 0, 1, 7, 8 }
			: new int[] { 0, 1, 2, 8, 9 };

		return new[] {
			dbResult.Recs[0].Where((x, i) => keep.Contains(i)).ToArray(),
		};
	}

	[Test]
	public async Task expired_prepares_are_scavenged_but_the_last_in_stream_is_physically_kept()
	{
		await CheckRecords();
	}
}
