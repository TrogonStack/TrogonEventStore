using System;
using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Core.Metrics;
using EventStore.Core.TransactionLog.Scavenging;
using EventStore.Core.XUnit.Tests.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class ScavengeStatusTrackerTests : IDisposable
{
	private readonly TestMeterListener<long> _listener;
	private readonly StatusMetric _metric;
	private readonly ScavengeStatusTracker _sut;

	public ScavengeStatusTrackerTests()
	{
		var meter = new Meter($"{typeof(ScavengeStatusTrackerTests)}");
		_listener = new TestMeterListener<long>(meter);
		_metric = new StatusMetric(
			meter,
			MetricDefinitions.TrogonEventstoreComponentStatus);
		_sut = new ScavengeStatusTracker(_metric);
	}

	public void Dispose()
	{
		_listener?.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void can_observe_activity()
	{
		AssertMeasurements("Idle");

		using (_sut.StartActivity("Accumulation"))
		{
			AssertMeasurements("Accumulation Phase");
		}

		AssertMeasurements("Idle");
	}

	void AssertMeasurements(string expectedStatus)
	{
		_listener.Observe();

		var measurements = _listener.RetrieveMeasurements(MetricDefinitions.TrogonEventstoreComponentStatus.Name);
		var measurement = Assert.Single(measurements, candidate => candidate.Value == 1);
		Assert.All(measurements.Where(candidate => candidate != measurement), candidate => Assert.Equal(0, candidate.Value));
		Assert.Collection(
			measurement.Tags.ToArray(),
			t =>
			{
				Assert.Equal(TrogonAttributeNames.ComponentName, t.Key);
				Assert.Equal("scavenge", t.Value);
			},
			t =>
			{
				Assert.Equal(TrogonAttributeNames.ComponentStatus, t.Key);
				Assert.Equal(expectedStatus.ToLowerInvariant(), t.Value);
			});
	}
}
