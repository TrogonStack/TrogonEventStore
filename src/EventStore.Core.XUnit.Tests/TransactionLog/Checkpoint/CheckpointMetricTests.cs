using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.XUnit.Tests.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.TransactionLog.Checkpoint;

public class CheckpointMetricTests
{
	[Fact]
	public void can_collect()
	{
		using var meter = new Meter($"{typeof(CheckpointMetricTests)}");
		using var listener = new TestMeterListener<long>(meter);
		var metric = new CheckpointMetric(
			meter,
			MetricDefinitions.TrogonEventstoreCheckpointPosition,
			new InMemoryCheckpoint("checkpoint", 5));

		listener.Observe();
		var measurement = Assert.Single(listener.RetrieveMeasurements(
			MetricDefinitions.TrogonEventstoreCheckpointPosition.Name));
		Assert.Equal(5, measurement.Value);
		Assert.Collection(
			measurement.Tags.ToArray(),
			tag =>
			{
				Assert.Equal(TrogonAttributeNames.CheckpointName, tag.Key);
				Assert.Equal("checkpoint", tag.Value);
			},
			tag =>
			{
				Assert.Equal(TrogonAttributeNames.CheckpointReadKind, tag.Key);
				Assert.Equal("non_flushed", tag.Value);
			});
	}
}
