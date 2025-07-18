using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.Scavenge;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class WhenDeletingDuplicateEvents<TLogFormat, TStreamId>()
	: ReadIndexTestScenario<TLogFormat, TStreamId>(indexBitnessVersion: EventStore.Core.Index.PTableVersions.IndexV1,
		performAdditionalChecks: false)
{
	private EventRecord _event1;
	private EventRecord _event2;
	private EventRecord _event3;
	private EventRecord _event4;
	private EventRecord _event5;
	private EventRecord _event6;
	private EventRecord _event7;
	private EventRecord _event8;

	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		_event1 = await WriteSingleEvent("account--696193173", 0, new string('.', 3000), retryOnFail: true,
			token: token);
		await WriteSingleEvent("account--696193173", 0, new string('.', 3000), retryOnFail: true, token: token);

		_event2 = await WriteSingleEvent("LPN-FC002_LPK51001", 0, new string('.', 3000), retryOnFail: true,
			token: token);
		await WriteSingleEvent("LPN-FC002_LPK51001", 0, new string('.', 3000), retryOnFail: true, token: token);

		_event3 = await WriteSingleEvent("account--696193173", 1, new string('.', 3000), retryOnFail: true,
			token: token);
		await WriteSingleEvent("account--696193173", 1, new string('.', 3000), retryOnFail: true, token: token);

		_event4 = await WriteSingleEvent("LPN-FC002_LPK51001", 1, new string('.', 3000), retryOnFail: true,
			token: token);
		await WriteSingleEvent("LPN-FC002_LPK51001", 1, new string('.', 3000), retryOnFail: true, token: token);

		_event5 = await WriteSingleEvent("account--696193173", 2, new string('.', 3000), retryOnFail: true,
			token: token);
		await WriteSingleEvent("account--696193173", 2, new string('.', 3000), retryOnFail: true, token: token);

		_event6 = await WriteSingleEvent("LPN-FC002_LPK51001", 2, new string('.', 3000), retryOnFail: true,
			token: token);
		await WriteSingleEvent("LPN-FC002_LPK51001", 2, new string('.', 3000), retryOnFail: true, token: token);

		_event7 = await WriteSingleEvent("account--696193173", 3, new string('.', 3000), retryOnFail: true,
			token: token);
		await WriteSingleEvent("account--696193173", 3, new string('.', 3000), retryOnFail: true, token: token);

		_event8 = await WriteSingleEvent("LPN-FC002_LPK51001", 3, new string('.', 3000), retryOnFail: true,
			token: token);
		await WriteSingleEvent("LPN-FC002_LPK51001", 3, new string('.', 3000), retryOnFail: true, token: token);

		await WriteSingleEvent("RandomStream", 0, new string('.', 3000), retryOnFail: true, token: token);
		await WriteSingleEvent("RandomStream", 1, new string('.', 3000), retryOnFail: true, token: token);

		Scavenge(completeLast: false, mergeChunks: false);
	}

	[Test]
	public void read_all_events_forward_does_not_return_duplicate()
	{
		var events = ReadIndex.ReadAllEventsForward(new TFPos(0, 0), 100).EventRecords()
			.Select(r => r.Event)
			.ToArray();
		Assert.AreEqual(11, events.Length);
		Assert.AreEqual(_event1, events[0]);
		Assert.AreEqual(_event2, events[1]);
		Assert.AreEqual(_event3, events[2]);
		Assert.AreEqual(_event4, events[3]);
		Assert.AreEqual(_event5, events[4]);
		Assert.AreEqual(_event6, events[5]);
		Assert.AreEqual(_event7, events[6]);
		Assert.AreEqual(_event8, events[7]);
	}
}
