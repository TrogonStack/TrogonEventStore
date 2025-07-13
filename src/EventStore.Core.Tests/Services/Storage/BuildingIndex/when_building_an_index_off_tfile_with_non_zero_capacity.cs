using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.BuildingIndex;

[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class WhenBuildingAnIndexOffTfileWithNonZeroCapacity<TLogFormat, TStreamId>()
	: ReadIndexTestScenario<TLogFormat, TStreamId>(streamInfoCacheCapacity: 20)
{
	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		await GetOrReserve("test1", token);
		await GetOrReserve("test2", token);
		await GetOrReserve("test3", token);
	}

	[Test]
	public void the_stream_created_records_can_be_read()
	{
		var records = ReadIndex.ReadStreamEventsForward(SystemStreams.StreamsCreatedStream, 0, 20).Records;
		Assert.AreEqual(3, records.Length);
	}
}
