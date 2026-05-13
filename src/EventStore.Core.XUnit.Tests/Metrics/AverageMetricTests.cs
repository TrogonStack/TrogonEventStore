using System.Diagnostics.Metrics;
using EventStore.Core.Metrics;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class AverageMetricTests
{
	[Fact]
	public void calculates_average()
	{
		using var meter = new Meter($"{typeof(QueueProcessingTrackerTests)}");
		using var listener = new TestMeterListener<double>(meter);
		var sut = new AverageMetric(meter, "the-metric", "seconds", label => new("queue", label));
		sut.Register("readers", () => 1);
		sut.Register("readers", () => 2);
		sut.Register("writer", () => 3);
		listener.Observe();

		Assert.Collection(
			listener.RetrieveMeasurements("the-metric-seconds"),
				m =>
				{
					Assert.Equal(1.5, m.Value);
					var tag = Assert.Single(m.Tags);
					Assert.Equal("queue", tag.Key);
					Assert.Equal("readers", tag.Value);
				},
				m =>
				{
					Assert.Equal(3, m.Value);
					var tag = Assert.Single(m.Tags);
					Assert.Equal("queue", tag.Key);
					Assert.Equal("writer", tag.Value);
				});
	}
}
