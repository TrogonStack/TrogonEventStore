using System;
using System.Collections.Generic;
using System.Linq;
using EventStore.Core.Tests;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Services;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.projections_manager;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class when_posting_a_transient_projection<TLogFormat, TStreamId> : TestFixtureWithProjectionCoreAndManagementServices<TLogFormat, TStreamId>
{
	private const string ProjectionName = "test-projection";
	private const string ProjectionBody = "fromAll().when({$any:function(s,e){return s;}});";

	protected override void Given()
	{
		AllWritesSucceed();
		NoOtherStreams();
	}

	protected override IEnumerable<WhenStep> When()
	{
		yield return new ProjectionSubsystemMessage.StartComponents(Guid.NewGuid());
		yield return new ProjectionManagementMessage.Command.Post(
			GetInputQueue(),
			ProjectionMode.Transient,
			ProjectionName,
			ProjectionManagementMessage.RunAs.System,
			"JS",
			ProjectionBody,
			enabled: true,
			checkpointsEnabled: false,
			emitEnabled: false,
			trackEmittedStreams: false);
	}

	[Test, Category("v8")]
	public void the_operation_fails()
	{
		var failure = HandledMessages.OfType<ProjectionManagementMessage.OperationFailed>().Single();

		Assert.That(failure.Reason, Does.Contain("Transient projections are not supported"));
	}

	[Test, Category("v8")]
	public void the_projection_is_not_registered()
	{
		GetInputQueue().Publish(new ProjectionManagementMessage.Command.GetQuery(
			Envelope,
			ProjectionName,
			ProjectionManagementMessage.RunAs.System));
		_queue.Process();

		Assert.IsTrue(HandledMessages.OfType<ProjectionManagementMessage.NotFound>().Any());
	}
}
