using System;
using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Core.Services.Archive;
using EventStore.Core.XUnit.Tests.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive;

public class ArchiveMetricsTests
{
	[Fact]
	public void observes_checkpoint_lag_and_pending_chunks()
	{
		using var meter = new Meter($"{typeof(ArchiveMetricsTests)}");
		using var listener = new TestMeterListener<long>(meter);
		var sut = new ArchiveMetrics(meter);

		sut.SetReplicationPosition(500);
		sut.SetCheckpoint(125);
		sut.SetUncommittedChunks(2);
		sut.SetQueuedChunks(3);
		sut.SetActiveChunks(1);
		listener.Observe();

		Assert.Equal(375, Assert.Single(listener.RetrieveMeasurements(
			MetricDefinitions.TrogonEventstoreArchiveCheckpointLag.Name)).Value);
		Assert.Equal(6, Assert.Single(listener.RetrieveMeasurements(
			MetricDefinitions.TrogonEventstoreArchiveChunkPendingCount.Name)).Value);
	}

	[Fact]
	public void checkpoint_lag_never_reports_a_negative_value()
	{
		using var meter = new Meter($"{typeof(ArchiveMetricsTests)}-negative-lag");
		using var listener = new TestMeterListener<long>(meter);
		var sut = new ArchiveMetrics(meter);

		sut.SetReplicationPosition(100);
		sut.SetCheckpoint(200);
		listener.Observe();

		Assert.Equal(0, Assert.Single(listener.RetrieveMeasurements(
			MetricDefinitions.TrogonEventstoreArchiveCheckpointLag.Name)).Value);
	}

	[Fact]
	public void does_not_publish_archiver_state_for_archive_readers()
	{
		using var meter = new Meter($"{typeof(ArchiveMetricsTests)}-reader");
		using var listener = new TestMeterListener<long>(meter);
		_ = new ArchiveMetrics(meter, observeArchiverState: false);

		listener.Observe();

		Assert.Empty(listener.RetrieveMeasurements(
			MetricDefinitions.TrogonEventstoreArchiveCheckpointLag.Name));
		Assert.Empty(listener.RetrieveMeasurements(
			MetricDefinitions.TrogonEventstoreArchiveChunkPendingCount.Name));
	}

	[Fact]
	public void records_retries_and_failures_by_operation()
	{
		using var meter = new Meter($"{typeof(ArchiveMetricsTests)}-counts");
		using var listener = new TestMeterListener<long>(meter);
		var sut = new ArchiveMetrics(meter);

		sut.RecordRetry(ArchiveOperation.StoreChunk);
		sut.RecordFailure(ArchiveOperation.ReadRange);

		var retry = Assert.Single(listener.RetrieveMeasurements(
			MetricDefinitions.TrogonEventstoreArchiveRetryCount.Name));
		Assert.Equal(1, retry.Value);
		Assert.Contains(retry.Tags, tag =>
			tag.Key == TrogonAttributeNames.ActivityName && (string)tag.Value == "store-chunk");

		var failure = Assert.Single(listener.RetrieveMeasurements(
			MetricDefinitions.TrogonEventstoreArchiveFailureCount.Name));
		Assert.Equal(1, failure.Value);
		Assert.Contains(failure.Tags, tag =>
			tag.Key == TrogonAttributeNames.ActivityName && (string)tag.Value == "read-range");
	}

	[Fact]
	public void records_remote_read_duration_and_outcome()
	{
		using var meter = new Meter($"{typeof(ArchiveMetricsTests)}-duration");
		using var listener = new TestMeterListener<double>(meter);
		var sut = new ArchiveMetrics(meter);

		sut.RecordRead(ArchiveOperation.ReadFull, TimeSpan.FromMilliseconds(1250), succeeded: true);

		var measurement = Assert.Single(listener.RetrieveMeasurements(
			MetricDefinitions.TrogonEventstoreArchiveReadDuration.Name));
		Assert.Equal(1.25, measurement.Value);
		Assert.Equal(
			[(TrogonAttributeNames.ActivityName, "read-full"), (TrogonAttributeNames.ActivityOutcome, "success")],
			measurement.Tags.Select(tag => (tag.Key, (string)tag.Value)));
	}
}
