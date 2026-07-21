using System;
using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Core.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class MaxTrackerTests : IDisposable
{
	private readonly TestMeterListener<long> _listener;
	private readonly FakeClock _clock = new();
	private readonly MaxTracker<long> _sut;
	private readonly MetricDefinition _definition =
		MetricDefinitions.TrogonEventstoreWriterFlushSizeMax;

	public MaxTrackerTests()
	{
		var meter = new Meter($"{typeof(MaxTrackerTests)}");
		_listener = new TestMeterListener<long>(meter);
		var metric = new MaxMetric<long>(meter, _definition);
		_sut = new MaxTracker<long>(
			metric: metric,
			name: "the-tracker",
			expectedScrapeIntervalSeconds: 15,
			clock: _clock);
	}

	public void Dispose()
	{
		_listener.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void no_records()
	{
		AssertMeasurements(0);
	}

	[Fact]
	public void two_records_ascending()
	{
		AssertMeasurements(0);
		_sut.Record(1);
		AssertMeasurements(1);
		_sut.Record(2);
		AssertMeasurements(2);
	}

	[Fact]
	public void two_records_descending()
	{
		AssertMeasurements(0);
		_sut.Record(2);
		AssertMeasurements(2);
		_sut.Record(1);
		AssertMeasurements(2);
	}

	[Fact]
	public void removes_stale_data()
	{
		_sut.Record(1);
		_clock.AdvanceSeconds(19);
		AssertMeasurements(1);

		_clock.AdvanceSeconds(1);
		AssertMeasurements(0);
	}

	void AssertMeasurements(double expectedValue)
	{
		_listener.Observe();

		var measurement = Assert.Single(_listener.RetrieveMeasurements(_definition.Name));
		Assert.Equal(expectedValue, measurement.Value);
		Assert.Collection(
			measurement.Tags.ToArray(),
			t =>
			{
				Assert.Equal(TrogonAttributeNames.QueueName, t.Key);
				Assert.Equal("the-tracker", t.Value);
			},
			t =>
			{
				Assert.Equal(TrogonAttributeNames.MeasurementWindow, t.Key);
				Assert.Equal("16-20s", t.Value);
			});
	}

	[Fact]
	public void no_name()
	{
		using var meter = new Meter($"{typeof(MaxTrackerTests)}");
		using var listener = new TestMeterListener<long>(meter);
		var sut = new MaxTracker<long>(
			metric: new MaxMetric<long>(meter, _definition),
			name: null,
			expectedScrapeIntervalSeconds: 15);

		listener.Observe();

		var measurement = Assert.Single(listener.RetrieveMeasurements(_definition.Name));
		Assert.Equal(0, measurement.Value);
		var tag = Assert.Single(measurement.Tags.ToArray());
		Assert.Equal(TrogonAttributeNames.MeasurementWindow, tag.Key);
		Assert.Equal("16-20s", tag.Value);
	}
}
