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
public class when_having_stream_with_max_count_specified<TLogFormat, TStreamId> : ScavengeTestScenario<TLogFormat, TStreamId>
{
	protected override ValueTask<DbResult> CreateDb(TFChunkDbCreationHelper<TLogFormat, TStreamId> dbCreator, CancellationToken token)
	{
		return dbCreator
			.Chunk(Rec.Prepare(0, "$$bla", metadata: new StreamMetadata(maxCount: 3)),
				Rec.Commit(0, "$$bla"),
				Rec.Prepare(1, "bla"),
				Rec.Commit(1, "bla"),
				Rec.Prepare(2, "bla"),
				Rec.Prepare(2, "bla"),
				Rec.Prepare(2, "bla"),
				Rec.Prepare(2, "bla"),
				Rec.Prepare(2, "bla"),
				Rec.Commit(2, "bla"),
				Rec.Prepare(3, "bla"),
				Rec.Prepare(3, "bla"),
				Rec.Prepare(3, "bla"),
				Rec.Prepare(3, "bla"),
				Rec.Commit(3, "bla"))
			.CompleteLastChunk()
			.CreateDb(token: token);
	}

	protected override ILogRecord[][] KeptRecords(DbResult dbResult)
	{
		var keep = LogFormatHelper<TLogFormat, TStreamId>.IsV2
			? new int[] { 0, 1, 11, 12, 13, 14 }
			: new int[] { 0, 1, 2, 12, 13, 14, 15 };

		return new[] {
			dbResult.Recs[0].Where((x, i) => keep.Contains(i)).ToArray(),
		};
	}

	[Test]
	public async Task expired_prepares_are_scavenged()
	{
		await CheckRecords();
	}
}
