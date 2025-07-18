using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EventStore.Common.Options;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Telemetry;
using EventStore.Core.Tests;
using EventStore.Core.Tests.Services.TimeService;
using EventStore.Core.Util;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Metrics;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Management;
using EventStore.Projections.Core.Services.Processing;
using EventStore.Projections.Core.Tests.Services.core_projection;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.projections_manager;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class when_starting_the_projection_manager_with_existing_projection<TLogFormat, TStreamId> : TestFixtureWithExistingEvents<TLogFormat, TStreamId>
{
	private new ITimeProvider _timeProvider;
	private ProjectionManager _manager;
	private Guid _workerId;

	protected override void Given()
	{
		_workerId = Guid.NewGuid();
		ExistingEvent(ProjectionNamesBuilder.ProjectionsRegistrationStream, ProjectionEventTypes.ProjectionCreated,
			null, "projection1");
		ExistingEvent(
			"$projections-projection1", ProjectionEventTypes.ProjectionUpdated, null,
			@"{""Query"":""fromAll(); on_any(function(){});log('hello-from-projection-definition');"", ""Mode"":""3"", ""Enabled"":true, ""HandlerType"":""JS""}");
	}


	[SetUp]
	public void setup()
	{
		_timeProvider = new FakeTimeProvider();
		var queues = new Dictionary<Guid, IPublisher> { { _workerId, _bus } };
		_manager = new ProjectionManager(
			_bus,
			_bus,
			queues,
			_timeProvider,
			ProjectionType.All,
			_ioDispatcher,
			TimeSpan.FromMinutes(Opts.ProjectionsQueryExpiryDefault),
			IProjectionTracker.NoOp);
		_bus.Subscribe<ClientMessage.WriteEventsCompleted>(_manager);
		_bus.Subscribe<ClientMessage.ReadStreamEventsBackwardCompleted>(_manager);
		_bus.Subscribe<ClientMessage.ReadStreamEventsForwardCompleted>(_manager);
		_manager.Handle(new ProjectionSubsystemMessage.StartComponents(Guid.NewGuid()));
	}

	[TearDown]
	public void TearDown()
	{
		_manager.Dispose();
	}

	[Test]
	public void projection_status_can_be_retrieved()
	{
		_manager.Handle(
			new ProjectionManagementMessage.Command.GetStatistics(_bus, null, "projection1",
				true));
		Assert.IsNotNull(
			_consumer.HandledMessages.OfType<ProjectionManagementMessage.Statistics>().SingleOrDefault(
				v => v.Projections[0].Name == "projection1"));
	}

	[Test]
	public void projection_status_is_starting()
	{
		_manager.Handle(
			new ProjectionManagementMessage.Command.GetStatistics(_bus, null, "projection1",
				true));
		Assert.AreEqual(
			ManagedProjectionState.Preparing,
			_consumer.HandledMessages.OfType<ProjectionManagementMessage.Statistics>().SingleOrDefault(
				v => v.Projections[0].Name == "projection1").Projections[0].LeaderStatus);
	}

	[Test]
	public void projection_telemetry_working()
	{
		_manager.Handle(
			new TelemetryMessage.Request(
				_bus));

		var actual = _consumer.HandledMessages.OfType<TelemetryMessage.Response>().Single();

		Assert.AreEqual("projections", actual.Key);
		Assert.AreEqual(
			new JsonObject
			{
				["customProjectionCount"] = 1,
				["standardProjectionCount"] = 0,
				["customProjectionRunningCount"] = 0,
				["standardProjectionRunningCount"] = 0,
				["totalCount"] = 1,
				["totalRunningCount"] = 0,
			}.ToJsonString(),
			actual.Value.ToJsonString());
	}
}
