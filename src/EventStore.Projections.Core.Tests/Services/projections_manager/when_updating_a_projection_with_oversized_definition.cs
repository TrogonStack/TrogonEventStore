using System;
using System.Collections.Generic;
using System.Linq;
using EventStore.Core.Messages;
using EventStore.Core.Tests;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Management;
using EventStore.Projections.Core.Services.Processing;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.projections_manager;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class when_updating_a_projection_with_oversized_definition<TLogFormat, TStreamId> : TestFixtureWithProjectionCoreAndManagementServices<TLogFormat, TStreamId>
{
	private string _projectionName;
	private readonly string _oversizedQuery = new('a', TFConsts.EffectiveMaxLogRecordSize);

	protected override void Given()
	{
		_projectionName = "test-projection";
		NoStream("$projections-test-projection");
		NoStream("$projections-test-projection-result");
		NoStream("$projections-test-projection-order");
		AllWritesToSucceed("$projections-test-projection-order");
		NoStream("$projections-test-projection-checkpoint");
		AllWritesSucceed();
	}

	protected override IEnumerable<WhenStep> When()
	{
		yield return new ProjectionSubsystemMessage.StartComponents(Guid.NewGuid());
		yield return
			new ProjectionManagementMessage.Command.Post(
				_bus, ProjectionMode.Continuous, _projectionName,
				ProjectionManagementMessage.RunAs.System, "JS", @"fromAll().when({$any:function(s,e){return s;}});",
				enabled: true, checkpointsEnabled: true, emitEnabled: true, trackEmittedStreams: true);
		yield return
			new ProjectionManagementMessage.Command.UpdateQuery(
				_bus, _projectionName, ProjectionManagementMessage.RunAs.System,
				_oversizedQuery, emitEnabled: null);
	}

	[Test, Category("v8")]
	public void the_operation_fails_as_record_too_large()
	{
		var failure = _consumer.HandledMessages.OfType<ProjectionManagementMessage.RecordTooLarge>().Single();
		StringAssert.Contains("exceeds the maximum", failure.Reason);
	}

	[Test, Category("v8")]
	public void no_second_definition_event_is_written()
	{
		var persistedStateStream = ProjectionNamesBuilder.ProjectionsStreamPrefix + _projectionName;
		Assert.AreEqual(1, _consumer.HandledMessages.OfType<ClientMessage.WriteEvents>()
			.Count(x => x.EventStreamId == persistedStateStream &&
						x.Events[0].EventType == ProjectionEventTypes.ProjectionUpdated));
	}

	[Test, Category("v8")]
	public void the_projection_is_faulted()
	{
		_manager.Handle(new ProjectionManagementMessage.Command.GetStatistics(_bus, null, _projectionName, true));
		Assert.AreEqual(
			ManagedProjectionState.Faulted,
			_consumer.HandledMessages.OfType<ProjectionManagementMessage.Statistics>().Last().Projections.Single()
				.LeaderStatus);
	}

	[Test, Category("v8")]
	public void the_core_projection_is_disposed()
	{
		Assert.AreEqual(1, _consumer.HandledMessages.OfType<CoreProjectionManagementMessage.Dispose>().Count());
	}
}
