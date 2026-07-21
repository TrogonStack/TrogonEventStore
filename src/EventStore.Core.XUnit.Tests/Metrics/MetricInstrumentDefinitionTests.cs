using System.Collections.Generic;
using System.Diagnostics.Metrics;
using EventStore.Common.Configuration;
using EventStore.Core.Metrics;
using EventStore.Core.TransactionLog.Checkpoint;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class MetricInstrumentDefinitionTests
{
	[Fact]
	public void wrappers_apply_complete_metric_definitions()
	{
		using var meter = new Meter($"{typeof(MetricInstrumentDefinitionTests)}");
		using var listener = new TestMeterListener<long>(meter);

		_ = new DurationMetric(
			meter,
			MetricDefinitions.TrogonEventstoreQueueMessageProcessingDuration);
		_ = new SummedCounterMetric(
			meter,
			MetricDefinitions.TrogonEventstoreQueueBusyTime,
			label => new(TrogonAttributeNames.QueueName, label));
		_ = new ObservableUpDownMetric<int>(
			meter,
			MetricDefinitions.TrogonEventstoreQueueMessageCount);
		_ = new CounterMetric(
			meter,
			MetricDefinitions.TrogonEventstoreClusterElectionCount);
		_ = new MaxMetric<long>(
			meter,
			MetricDefinitions.TrogonEventstoreWriterFlushSizeMax);
		_ = new DurationMaxMetric(
			meter,
			MetricDefinitions.TrogonEventstoreQueueMessageWaitDurationMax);
		_ = new StatusMetric(
			meter,
			MetricDefinitions.TrogonEventstoreComponentStatus);
		_ = new CacheHitsMissesMetric(
			meter,
			[],
			MetricDefinitions.TrogonEventstoreCacheOperationCount,
			new Dictionary<MetricsConfiguration.Cache, string>());
		using var incomingCalls = new IncomingGrpcCallsMetric(
			meter,
			MetricDefinitions.TrogonEventstoreGrpcServerCallActive,
			MetricDefinitions.TrogonEventstoreGrpcServerCallCount,
			MetricDefinitions.TrogonEventstoreGrpcServerCallFailureCount,
			MetricDefinitions.TrogonEventstoreGrpcServerCallUnimplementedCount,
			MetricDefinitions.TrogonEventstoreGrpcServerCallDeadlineExceededCount,
			[
				MetricsConfiguration.IncomingGrpcCall.Current,
				MetricsConfiguration.IncomingGrpcCall.Total,
				MetricsConfiguration.IncomingGrpcCall.Failed,
			]);
		_ = new CacheResourcesMetrics(
			meter,
			MetricDefinitions.TrogonEventstoreCacheResourceSize,
			MetricDefinitions.TrogonEventstoreCacheResourceCount);
		_ = new LogicalChunkReadDistributionMetric(
			meter,
			MetricDefinitions.TrogonEventstoreStorageChunkReadDistance,
			new InMemoryCheckpoint(),
			chunkSize: 1);
		_ = new CheckpointMetric(
			meter,
			MetricDefinitions.TrogonEventstoreCheckpointPosition);

		AssertInstrument<Histogram<double>>(
			listener,
			MetricDefinitions.TrogonEventstoreQueueMessageProcessingDuration);
		AssertInstrument<ObservableCounter<double>>(
			listener,
			MetricDefinitions.TrogonEventstoreQueueBusyTime);
		AssertInstrument<ObservableUpDownCounter<int>>(
			listener,
			MetricDefinitions.TrogonEventstoreQueueMessageCount);
		AssertInstrument<ObservableCounter<long>>(
			listener,
			MetricDefinitions.TrogonEventstoreClusterElectionCount);
		AssertInstrument<ObservableGauge<long>>(
			listener,
			MetricDefinitions.TrogonEventstoreWriterFlushSizeMax);
		AssertInstrument<ObservableGauge<double>>(
			listener,
			MetricDefinitions.TrogonEventstoreQueueMessageWaitDurationMax);
		AssertInstrument<ObservableUpDownCounter<long>>(
			listener,
			MetricDefinitions.TrogonEventstoreComponentStatus);
		AssertInstrument<ObservableCounter<long>>(
			listener,
			MetricDefinitions.TrogonEventstoreCacheOperationCount);
		AssertInstrument<ObservableUpDownCounter<long>>(
			listener,
			MetricDefinitions.TrogonEventstoreGrpcServerCallActive);
		AssertInstrument<ObservableCounter<long>>(
			listener,
			MetricDefinitions.TrogonEventstoreGrpcServerCallCount);
		AssertInstrument<ObservableCounter<long>>(
			listener,
			MetricDefinitions.TrogonEventstoreGrpcServerCallFailureCount);
		AssertInstrument<ObservableUpDownCounter<long>>(
			listener,
			MetricDefinitions.TrogonEventstoreCacheResourceSize);
		AssertInstrument<ObservableUpDownCounter<long>>(
			listener,
			MetricDefinitions.TrogonEventstoreCacheResourceCount);
		AssertInstrument<Histogram<long>>(
			listener,
			MetricDefinitions.TrogonEventstoreStorageChunkReadDistance);
		AssertInstrument<ObservableGauge<long>>(
			listener,
			MetricDefinitions.TrogonEventstoreCheckpointPosition);
	}

	private static void AssertInstrument<TInstrument>(
		TestMeterListener<long> listener,
		MetricDefinition definition)
		where TInstrument : Instrument
	{
		var instrument = listener.GetInstrument(definition.Name);
		Assert.IsType<TInstrument>(instrument);
		Assert.Equal(definition.InstrumentKind, GetInstrumentKind(instrument));
		Assert.Equal(definition.Name, instrument.Name);
		Assert.Equal(definition.Unit, instrument.Unit);
		Assert.Equal(definition.Description, instrument.Description);
	}

	private static MetricInstrumentKind GetInstrumentKind(Instrument instrument)
	{
		var instrumentType = instrument.GetType().GetGenericTypeDefinition();
		if (instrumentType == typeof(Histogram<>))
		{
			return MetricInstrumentKind.Histogram;
		}

		if (instrumentType == typeof(ObservableCounter<>))
		{
			return MetricInstrumentKind.Counter;
		}

		if (instrumentType == typeof(ObservableGauge<>))
		{
			return MetricInstrumentKind.Gauge;
		}

		if (instrumentType == typeof(ObservableUpDownCounter<>))
		{
			return MetricInstrumentKind.UpDownCounter;
		}

		throw new Xunit.Sdk.XunitException($"Unsupported instrument type '{instrumentType}'.");
	}
}
