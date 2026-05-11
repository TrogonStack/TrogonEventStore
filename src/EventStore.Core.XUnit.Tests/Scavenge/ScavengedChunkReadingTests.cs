using System.Linq;
using System.Threading.Tasks;
using EventStore.Core.Tests;
using EventStore.Core.Tests.TransactionLog.Scavenging.Helpers;
using EventStore.Core.XUnit.Tests.Scavenge.Sqlite;
using Xunit;
using static EventStore.Core.XUnit.Tests.Scavenge.StreamMetadatas;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class ScavengedChunkReadingTests : SqliteDbPerTest<ScavengedChunkReadingTests>
{
	[Fact]
	public async Task missing_midpoint()
	{
		var t = 0;
		await new Scenario<LogFormat.V2, string>()
			.WithDbPath(Fixture.Directory)
			.SkipIndexCheck()
			.WithDb(x => x
				.Chunk([
					Rec.Write(t++, "ab-1"),
					Rec.Write(t++, "ab-1"),
					..Enumerable.Range(0, 2738).Select(_ => Rec.Write(t++, "cd-1")),
				])
				.Chunk(
					Rec.Write(t++, "$$ab-1", "$metadata", metadata: SoftDelete),
					ScavengePointRec(t++)))
			.WithState(x => x.WithConnectionPool(Fixture.DbConnectionPool))
			.RunAsync(x => [
				x.Recs[0][1..],
				x.Recs[1],
			]);
	}
}
