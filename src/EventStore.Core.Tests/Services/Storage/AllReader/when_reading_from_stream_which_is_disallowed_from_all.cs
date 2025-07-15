using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.AllReader;

[TestFixture(typeof(LogFormat.V2), typeof(string), "$persistentsubscription-$all::group-checkpoint")]
[TestFixture(typeof(LogFormat.V2), typeof(string), "$persistentsubscription-$all::group-parked")]
[TestFixture(typeof(LogFormat.V3), typeof(uint), "$persistentsubscription-$all::group-checkpoint")]
[TestFixture(typeof(LogFormat.V3), typeof(uint), "$persistentsubscription-$all::group-parked")]
public class WhenReadingFromStreamWhichIsDisallowedFromAll<TLogFormat, TStreamId>(string stream)
	: ReadIndexTestScenario<TLogFormat, TStreamId>
{
	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		await WriteSingleEvent(stream, 1, new string('.', 3000), eventId: Guid.NewGuid(),
			eventType: "event-type-1", retryOnFail: true, token: token);
		await WriteSingleEvent(stream, 2, new string('.', 3000), eventId: Guid.NewGuid(),
			eventType: "event-type-1", retryOnFail: true, token: token);
	}

	[Test]
	public async Task should_be_able_to_read_stream_events_forward()
	{
		var result = await ReadIndex.ReadStreamEventsForward(stream, 0L, 10, CancellationToken.None);
		Assert.AreEqual(2, result.Records.Length);
	}

	[Test]
	public async Task should_be_able_to_read_stream_events_backward()
	{
		var result = await ReadIndex.ReadStreamEventsBackward(stream, -1, 10, CancellationToken.None);
		Assert.AreEqual(2, result.Records.Length);
	}
}
