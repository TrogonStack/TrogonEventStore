using System;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Services.Archive;
using EventStore.Core.Services.Archive.Storage.Exceptions;
using EventStore.Core.XUnit.Tests.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Storage;

#if RUN_S3_TESTS
public class S3ReaderTests : ArchiveStorageReaderTests<S3ReaderTests>
{
	protected override StorageType StorageType => StorageType.S3;
}

public class S3WriterTests : ArchiveStorageWriterTests<S3WriterTests>
{
	protected override StorageType StorageType => StorageType.S3;
}

public class S3MetricsTests : ArchiveStorageTestsBase<S3MetricsTests>
{
	protected override StorageType StorageType => StorageType.S3;

	[Fact]
	public async Task records_remote_reads_and_failures_through_the_s3_factory()
	{
		using var meter = new Meter($"{typeof(S3MetricsTests)}");
		using var durationListener = new TestMeterListener<double>(meter);
		using var failureListener = new TestMeterListener<long>(meter);
		var metrics = new ArchiveMetrics(meter);
		var factory = CreateSutFactory(StorageType, metrics);
		var writer = factory.CreateWriter();
		var reader = factory.CreateReader();
		var chunkFile = reader.ChunkNamer.GetFileNameFor(0);

		Assert.True(await writer.StoreChunk(
			CreateLocalChunk(0, 0),
			chunkFile,
			CancellationToken.None));

		await using (var stream = await reader.GetChunk(chunkFile, CancellationToken.None))
		{
			await stream.CopyToAsync(Stream.Null);
		}

		await using (var stream = await reader.GetChunk(chunkFile, 100, 200, CancellationToken.None))
		{
			await stream.CopyToAsync(Stream.Null);
		}

		await Assert.ThrowsAsync<ChunkDeletedException>(async () =>
		{
			await reader.GetChunk("missing-chunk", CancellationToken.None);
		});

		var durations = durationListener.RetrieveMeasurements(
			MetricDefinitions.TrogonEventstoreArchiveReadDuration.Name);
		Assert.Contains(durations, measurement => HasTags(measurement, "read-full", "success"));
		Assert.Contains(durations, measurement => HasTags(measurement, "read-range", "success"));
		Assert.Contains(durations, measurement => HasTags(measurement, "read-full", "error"));

		var failure = Assert.Single(failureListener.RetrieveMeasurements(
			MetricDefinitions.TrogonEventstoreArchiveFailureCount.Name));
		Assert.Equal(1, failure.Value);
		Assert.Contains(failure.Tags, tag =>
			tag.Key == TrogonAttributeNames.ActivityName && (string)tag.Value == "read-full");
	}

	private static bool HasTags(
		TestMeterListener<double>.TestMeasurement measurement,
		string activity,
		string outcome) =>
		measurement.Tags.Any(tag =>
			tag.Key == TrogonAttributeNames.ActivityName && (string)tag.Value == activity) &&
		measurement.Tags.Any(tag =>
			tag.Key == TrogonAttributeNames.ActivityOutcome && (string)tag.Value == outcome);
}
#endif
