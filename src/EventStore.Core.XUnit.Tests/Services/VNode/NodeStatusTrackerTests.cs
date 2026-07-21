using System;
using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Core.Metrics;
using EventStore.Core.Services.VNode;
using EventStore.Core.XUnit.Tests.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.TransactionLog.Services.VNode;

public class NodeStatusTrackerTests : IDisposable
{
	private readonly TestMeterListener<long> _listener;
	private readonly StatusMetric _metric;
	private readonly NodeStatusTracker _sut;

	public NodeStatusTrackerTests()
	{
		var meter = new Meter($"{typeof(NodeStatusTrackerTests)}");
		_listener = new TestMeterListener<long>(meter);
		_metric = new StatusMetric(
			meter,
			MetricDefinitions.TrogonEventstoreComponentStatus);
		_sut = new NodeStatusTracker(_metric);
	}

	public void Dispose()
	{
		_listener?.Dispose();
	}

	[Fact]
	public void can_observe_state_change()
	{
		AssertMeasurements("Initializing");

		_sut.OnStateChange(Data.VNodeState.PreReplica);
		AssertMeasurements("PreReplica");

		_sut.OnStateChange(Data.VNodeState.Follower);
		AssertMeasurements("Follower");
	}

	[Fact]
	public void previously_seen_states_are_reported_as_inactive()
	{
		_sut.OnStateChange(Data.VNodeState.PreReplica);
		_listener.Observe();

		var measurements = _listener.RetrieveMeasurements(MetricDefinitions.TrogonEventstoreComponentStatus.Name);
		Assert.Contains(
			measurements,
			measurement => measurement.Value == 0 &&
				measurement.Tags.Any(tag =>
					tag.Key == TrogonAttributeNames.ComponentStatus &&
					tag.Value as string == "initializing"));
	}

	[Fact]
	public void can_observe_initial()
	{
		_sut.OnStateChange(Data.VNodeState.PreLeader);
		_sut.OnStateChange(InaugurationManager.ManagerState.BecomingLeader);
		AssertMeasurements("PreLeader - BecomingLeader");

		_sut.OnStateChange(InaugurationManager.ManagerState.Idle);
		AssertMeasurements("PreLeader");
	}

	[Fact]
	public void can_observe_waiting_for_chaser()
	{
		_sut.OnStateChange(Data.VNodeState.PreLeader);
		AssertMeasurements("PreLeader");

		_sut.OnStateChange(InaugurationManager.ManagerState.WaitingForChaser);
		AssertMeasurements("PreLeader - WaitingForChaser");
	}

	[Fact]
	public void can_observe_writing_epoch()
	{
		_sut.OnStateChange(Data.VNodeState.PreLeader);
		AssertMeasurements("PreLeader");

		_sut.OnStateChange(InaugurationManager.ManagerState.WritingEpoch);
		AssertMeasurements("PreLeader - WritingEpoch");
	}

	[Fact]
	public void can_observe_waiting_for_conditions()
	{
		_sut.OnStateChange(Data.VNodeState.PreLeader);
		AssertMeasurements("PreLeader");

		_sut.OnStateChange(InaugurationManager.ManagerState.WaitingForConditions);
		AssertMeasurements("PreLeader - WaitingForConditions");
	}

	[Fact]
	public void can_observe_becoming_leader()
	{
		_sut.OnStateChange(Data.VNodeState.PreLeader);
		AssertMeasurements("PreLeader");

		_sut.OnStateChange(InaugurationManager.ManagerState.BecomingLeader);
		AssertMeasurements("PreLeader - BecomingLeader");
	}

	void AssertMeasurements(string expectedStatus)
	{
		_listener.Observe();

		var measurements = _listener.RetrieveMeasurements(MetricDefinitions.TrogonEventstoreComponentStatus.Name);
		var active = Assert.Single(measurements, measurement => measurement.Value == 1);
		Assert.All(measurements.Where(measurement => measurement != active), measurement => Assert.Equal(0, measurement.Value));
		Assert.Collection(
			active.Tags.ToArray(),
			t =>
			{
				Assert.Equal(TrogonAttributeNames.ComponentName, t.Key);
				Assert.Equal("node", t.Value);
			},
			t =>
			{
				Assert.Equal(TrogonAttributeNames.ComponentStatus, t.Key);
				Assert.Equal(expectedStatus.ToLowerInvariant(), t.Value);
			});
	}
}
