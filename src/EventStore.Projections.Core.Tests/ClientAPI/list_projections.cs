using System;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Core.Tests;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.ClientAPI;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class list_projections<TLogFormat, TStreamId> : specification_with_standard_projections_runnning<TLogFormat, TStreamId>
{
	const string TestProjection =
		"fromAll().when({$init: function (state, ev) {return {};},ConversationStarted: function (state, ev) {state.lastBatchSent = ev;return state;}});";

	[Test]
	public async Task list_all_projections_works()
	{
		var x = await ProjectionClient.StatisticsAll();
		Assert.AreEqual(true, x.Any());
		Assert.IsTrue(x.Any(p => p.Name == "$streams"));
	}

	[Test]
	public async Task list_oneTime_projections_works()
	{
		await ProjectionClient.CreateOneTime(TestProjection);
		var x = await ProjectionClient.StatisticsOneTime();
		Assert.AreEqual(true, x.Any(p => p.Mode == "OneTime"));
	}

	[Test]
	public async Task list_continuous_projections_works()
	{
		var nameToTest = Guid.NewGuid().ToString();
		await ProjectionClient.CreateContinuous(nameToTest, TestProjection);
		var x = await ProjectionClient.StatisticsContinuous();
		Assert.AreEqual(true, x.Any(p => p.Name == nameToTest));
	}
}
