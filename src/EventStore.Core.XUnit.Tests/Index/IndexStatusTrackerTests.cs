using System;
using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Core.Index;
using EventStore.Core.Metrics;
using EventStore.Core.XUnit.Tests.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Index;

public class IndexStatusTrackerTests : IDisposable
{
	private readonly TestMeterListener<long> _listener;
	private readonly StatusMetric _metric;
	private readonly IndexStatusTracker _sut;

	public IndexStatusTrackerTests()
	{
		var meter = new Meter($"{typeof(IndexStatusTrackerTests)}");
		_listener = new TestMeterListener<long>(meter);
		_metric = new StatusMetric(
			meter,
			MetricDefinitions.TrogonEventstoreComponentStatus);
		_sut = new IndexStatusTracker(_metric);
	}

	public void Dispose()
	{
		_listener.Dispose();
	}

	[Fact]
	public void can_observe_opening()
	{
		AssertMeasurements("Idle");

		using (_sut.StartOpening())
		{
			AssertMeasurements("Opening");
		}

		AssertMeasurements("Idle");
	}

	[Fact]
	public void can_observe_rebuilding()
	{
		AssertMeasurements("Idle");

		using (_sut.StartRebuilding())
		{
			AssertMeasurements("Rebuilding");
		}

		AssertMeasurements("Idle");
	}

	[Fact]
	public void can_observe_initializing()
	{
		AssertMeasurements("Idle");

		using (_sut.StartInitializing())
		{
			AssertMeasurements("Initializing");
		}

		AssertMeasurements("Idle");
	}

	[Fact]
	public void can_observe_merging()
	{
		AssertMeasurements("Idle");

		using (_sut.StartMerging())
		{
			AssertMeasurements("Merging");
		}

		AssertMeasurements("Idle");
	}

	[Fact]
	public void can_observe_scavenging()
	{
		AssertMeasurements("Idle");

		using (_sut.StartScavenging())
		{
			AssertMeasurements("Scavenging");
		}

		AssertMeasurements("Idle");
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
				Assert.Equal("index", t.Value);
			},
			t =>
			{
				Assert.Equal(TrogonAttributeNames.ComponentStatus, t.Key);
				Assert.Equal(expectedStatus.ToLowerInvariant(), t.Value);
			});
	}
}
