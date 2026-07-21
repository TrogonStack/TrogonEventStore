using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using EventStore.Core.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class QueueBusyTrackerTests
{
	[Fact]
	public async Task records()
	{
		using var meter = new Meter($"{typeof(QueueProcessingTrackerTests)}");
		using var listener = new TestMeterListener<double>(meter);
		var definition = MetricDefinitions.TrogonEventstoreQueueBusyTime;
		var metric = new SummedCounterMetric(
			meter,
			definition,
			label => new(TrogonAttributeNames.QueueName, label));
		var sut = new QueueBusyTracker(metric, "the-queue");

		sut.EnterBusy();
		await Task.Delay(1);
		sut.EnterIdle();
		listener.Observe();

		var measurement = Assert.Single(listener.RetrieveMeasurements(definition.Name));
		Assert.True(measurement.Value > 0.0001);
		var tag = Assert.Single(measurement.Tags);
		Assert.Equal(TrogonAttributeNames.QueueName, tag.Key);
		Assert.Equal("the-queue", tag.Value);
	}
}
