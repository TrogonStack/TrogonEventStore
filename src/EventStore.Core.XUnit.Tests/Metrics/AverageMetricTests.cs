using System.Diagnostics.Metrics;
using EventStore.Core.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class SummedCounterMetricTests
{
	[Fact]
	public void calculates_sum()
	{
		using var meter = new Meter($"{typeof(QueueProcessingTrackerTests)}");
		using var listener = new TestMeterListener<double>(meter);
		var definition = MetricDefinitions.TrogonEventstoreQueueBusyTime;
		var sut = new SummedCounterMetric(
			meter,
			definition,
			label => new(TrogonAttributeNames.QueueName, label));
		sut.Register("readers", () => 1);
		sut.Register("readers", () => 2);
		sut.Register("writer", () => 3);
		listener.Observe();

		Assert.Collection(
			listener.RetrieveMeasurements(definition.Name),
				m =>
				{
					Assert.Equal(3, m.Value);
					var tag = Assert.Single(m.Tags);
					Assert.Equal(TrogonAttributeNames.QueueName, tag.Key);
					Assert.Equal("readers", tag.Value);
				},
				m =>
				{
					Assert.Equal(3, m.Value);
					var tag = Assert.Single(m.Tags);
					Assert.Equal(TrogonAttributeNames.QueueName, tag.Key);
					Assert.Equal("writer", tag.Value);
				});
	}
}
