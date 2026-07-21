using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using EventStore.Core.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;
using Conf = EventStore.Common.Configuration.MetricsConfiguration;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class IncomingGrpcCallsMetricTests
{
	[EventSource(Name = nameof(IncomingGrpcCallsMetricTests))]
	private class TestEventSource : EventSource
	{
		[Event(1)] public void CallStart() => WriteEvent(1);
		[Event(2)] public void CallStop() => WriteEvent(2);
		[Event(3)] public void CallFailed() => WriteEvent(3);
		[Event(4)] public void CallDeadlineExceeded() => WriteEvent(4);
		[Event(5)] public void CallUnimplemented() => WriteEvent(5);
	}

	[Fact]
	public void reports_each_diagnostic_counter_without_overlapping_attributes()
	{
		using var testSource = new TestEventSource();
		using var meter = new Meter($"{typeof(IncomingGrpcCallsMetricTests)}");
		using var listener = new TestMeterListener<long>(meter);
		using var sut = CreateMetric(
			meter,
			[
				Conf.IncomingGrpcCall.Current,
				Conf.IncomingGrpcCall.Total,
				Conf.IncomingGrpcCall.Failed,
				Conf.IncomingGrpcCall.Unimplemented,
				Conf.IncomingGrpcCall.DeadlineExceeded,
			]);

		sut.EnableEvents(testSource, EventLevel.Verbose);
		testSource.CallStart();
		testSource.CallFailed();
		testSource.CallUnimplemented();
		testSource.CallDeadlineExceeded();
		listener.Observe();

		AssertScalar(listener, MetricDefinitions.TrogonEventstoreGrpcServerCallActive, 1);
		AssertScalar(listener, MetricDefinitions.TrogonEventstoreGrpcServerCallCount, 1);
		AssertScalar(listener, MetricDefinitions.TrogonEventstoreGrpcServerCallFailureCount, 1);
		AssertScalar(listener, MetricDefinitions.TrogonEventstoreGrpcServerCallUnimplementedCount, 1);
		AssertScalar(listener, MetricDefinitions.TrogonEventstoreGrpcServerCallDeadlineExceededCount, 1);

		testSource.CallStop();
		listener.Observe();
		AssertScalar(listener, MetricDefinitions.TrogonEventstoreGrpcServerCallActive, 0);
	}

	[Fact]
	public void publishes_only_configured_counters()
	{
		using var testSource = new TestEventSource();
		using var meter = new Meter($"{typeof(IncomingGrpcCallsMetricTests)}");
		using var listener = new TestMeterListener<long>(meter);
		using var sut = CreateMetric(
			meter,
			[
				Conf.IncomingGrpcCall.Total,
				Conf.IncomingGrpcCall.DeadlineExceeded,
			]);

		sut.EnableEvents(testSource, EventLevel.Verbose);
		testSource.CallStart();
		testSource.CallFailed();
		testSource.CallUnimplemented();
		testSource.CallDeadlineExceeded();
		testSource.CallStop();
		listener.Observe();

		Assert.Empty(listener.RetrieveMeasurements(MetricDefinitions.TrogonEventstoreGrpcServerCallActive.Name));
		Assert.Empty(listener.RetrieveMeasurements(MetricDefinitions.TrogonEventstoreGrpcServerCallFailureCount.Name));
		Assert.Empty(listener.RetrieveMeasurements(MetricDefinitions.TrogonEventstoreGrpcServerCallUnimplementedCount.Name));
		AssertScalar(listener, MetricDefinitions.TrogonEventstoreGrpcServerCallCount, 1);
		AssertScalar(listener, MetricDefinitions.TrogonEventstoreGrpcServerCallDeadlineExceededCount, 1);
	}

	private static IncomingGrpcCallsMetric CreateMetric(Meter meter, Conf.IncomingGrpcCall[] filter) =>
		new(
			meter,
			MetricDefinitions.TrogonEventstoreGrpcServerCallActive,
			MetricDefinitions.TrogonEventstoreGrpcServerCallCount,
			MetricDefinitions.TrogonEventstoreGrpcServerCallFailureCount,
			MetricDefinitions.TrogonEventstoreGrpcServerCallUnimplementedCount,
			MetricDefinitions.TrogonEventstoreGrpcServerCallDeadlineExceededCount,
			filter);

	private static void AssertScalar(
		TestMeterListener<long> listener,
		MetricDefinition definition,
		long expectedValue)
	{
		var measurement = Assert.Single(listener.RetrieveMeasurements(definition.Name));
		Assert.Equal(expectedValue, measurement.Value);
		Assert.Empty(measurement.Tags);
	}
}
