using System.Threading.Tasks;
using EventStore.Core.Tests;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.ClientAPI.when_handling_deleted.with_from_category_foreach_projection;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class when_running_and_events_are_indexed_including_tombstone<TLogFormat, TStreamId> :
	specification_with_standard_projections_runnning<TLogFormat, TStreamId>
{
	protected override bool GivenStandardProjectionsRunning()
	{
		return false;
	}

	protected override async Task Given()
	{
		await base.Given();
		await PostEvent("stream-1", "type1", "{}");
		await PostEvent("stream-2", "type1", "{}");
		await PostEvent("stream-2", "type2", "{}");
		await PostEvent("stream-1", "type2", "{}");
		await HardDeleteStream("stream-1");
		WaitIdle();
		await EnableStandardProjections();
		WaitIdle();
	}

	protected override async Task When()
	{
		await base.When();

		await PostProjection(@"
fromCategory('stream').foreachStream().when({
    $init: function(){return {a:0}},
    type1: function(s,e){s.a++},
    type2: function(s,e){s.a++},
    $deleted: function(s,e){s.deleted=1},
}).outputState();
");
		WaitIdle();
	}

	[Test, Category("Network")]
	[Ignore("Regression")]
	public async Task receives_deleted_notification()
	{
		await DumpStream("$ce-stream");
		await AssertStreamTail("$projections-test-projection-stream-1-result", "Result:{\"a\":0,\"deleted\":1}");
	}
}
