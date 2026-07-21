using System;
using System.Diagnostics.Metrics;
using EventStore.Core.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class DurationTrackerTests : IDisposable
{
	private readonly TestMeterListener<double> _listener;
	private readonly FakeClock _clock = new();
	private readonly DurationTracker _sut;
	private readonly MetricDefinition _definition =
		MetricDefinitions.TrogonEventstoreGossipExchangeDuration;

	public DurationTrackerTests()
	{
		var meter = new Meter($"{typeof(DurationTrackerTests)}");
		_listener = new TestMeterListener<double>(meter);
		var durationMetric = new DurationMetric(meter, _definition, _clock);
		_sut = new DurationTracker(durationMetric, "the-duration");
	}

	public void Dispose()
	{
		_listener.Dispose();
	}

	[Fact]
	public void records_success()
	{
		_clock.SecondsSinceEpoch = 500;
		using (_sut.Start())
		{
			_clock.SecondsSinceEpoch = 501;
		}

		AssertMeasurements("success", 1);
	}

	[Fact]
	public void records_failure()
	{
		_clock.SecondsSinceEpoch = 500;
		using (var duration = _sut.Start())
		{
			_clock.SecondsSinceEpoch = 501;
			duration.SetException(new Exception("failed"));
		}

		AssertMeasurements("error", 1);
	}

	void AssertMeasurements(
		string expectedStatus,
		int expectedValue)
	{

		Assert.Collection(
			_listener.RetrieveMeasurements(_definition.Name),
			m =>
			{
				Assert.Equal(expectedValue, m.Value);
				Assert.Collection(
					m.Tags,
					t =>
					{
						Assert.Equal(TrogonAttributeNames.ActivityName, t.Key);
						Assert.Equal("the-duration", t.Value);
					},
					t =>
					{
						Assert.Equal(TrogonAttributeNames.ActivityOutcome, t.Key);
						Assert.Equal(expectedStatus, t.Value);
					});
			});
	}
}
