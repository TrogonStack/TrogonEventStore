using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.MaxAgeMaxCount.ReadRangeAndNextEventNumber;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class
	WhenReadingVeryLongStreamWithMaxAgeAndMostlyExpiredEvents<TLogFormat, TStreamId>()
	: ReadIndexTestScenario<TLogFormat, TStreamId>(maxEntriesInMemTable: 500_000, chunkSize: TFConsts.ChunkSize)
{
	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		var now = DateTime.UtcNow;
		var metadata = $@"{{""$maxAge"":{(int)TimeSpan.FromMinutes(20).TotalSeconds}}}";
		await WriteStreamMetadata("ES", 0, metadata, now.AddMinutes(-100), token: token);
		for (int i = 0; i < 1_000_000; i++)
		{
			await WriteSingleEvent("ES", i, "bla", now.AddMinutes(-50), retryOnFail: true, token: token);
		}

		for (int i = 1_000_000; i < 1_000_015; i++)
		{
			await WriteSingleEvent("ES", i, "bla", now.AddMinutes(-1), retryOnFail: true, token: token);
		}
	}

	[Test, Explicit, Category("LongRunning")]
	public void on_read_from_beginning()
	{
		Stopwatch sw = Stopwatch.StartNew();
		var res = ReadIndex.ReadStreamEventsForward("ES", 1, 10);
		var elapsed = sw.Elapsed;

		Assert.AreEqual(1_000_000, res.NextEventNumber);
		Assert.AreEqual(0, res.Records.Length);
		Assert.AreEqual(false, res.IsEndOfStream);

		res = ReadIndex.ReadStreamEventsForward("ES", res.NextEventNumber, 10);

		Assert.AreEqual(1_000_010, res.NextEventNumber);
		Assert.AreEqual(10, res.Records.Length);
		Assert.AreEqual(false, res.IsEndOfStream);

		res = ReadIndex.ReadStreamEventsForward("ES", res.NextEventNumber, 10);

		Assert.AreEqual(1_000_015, res.NextEventNumber);
		Assert.AreEqual(5, res.Records.Length);
		Assert.AreEqual(true, res.IsEndOfStream);

		Assert.Less(elapsed, TimeSpan.FromSeconds(1));
	}
}
