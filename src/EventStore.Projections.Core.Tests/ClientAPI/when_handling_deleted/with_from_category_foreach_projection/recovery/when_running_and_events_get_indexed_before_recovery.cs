using System.Threading.Tasks;
using EventStore.Core.Tests;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.ClientAPI.when_handling_deleted.with_from_category_foreach_projection.
	recovery;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class
	when_running_and_events_get_indexed_before_recovery<TLogFormat, TStreamId>
	: specification_with_standard_projections_runnning<TLogFormat, TStreamId>
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
		await PostEvent("stream-1", "type2", "{}");
		await PostEvent("stream-2", "type2", "{}");
		WaitIdle();
		await EnableStandardProjections();
		WaitIdle();
		await PostProjection(@"
fromCategory('stream').foreachStream().when({
    $init: function(){return {a:0}},
    type1: function(s,e){s.a++},
    type2: function(s,e){s.a++},
    $deleted: function(s,e){s.deleted=1},
}).outputState();
");
		WaitIdle();
		await HardDeleteStream("stream-1");
		WaitIdle();
		await _manager.AbortAsync("test-projection", _admin);
		WaitIdle();
	}

	protected override async Task When()
	{
		await base.When();
		await _manager.EnableAsync("test-projection", _admin);
		WaitIdle();
	}

	[Test, Category("Network")]
	[Ignore("Regression")]
	public async Task receives_deleted_notification()
	{
		await AssertStreamTail("$projections-test-projection-stream-1-result", "Result:{\"a\":2,\"deleted\":1}");
		await AssertStreamTail("$projections-test-projection-stream-2-result", "Result:{\"a\":2}");
	}
}
