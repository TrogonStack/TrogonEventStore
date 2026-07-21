using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Common.Configuration;
using EventStore.Core.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class ProcessMetricsTests : IDisposable
{
	private readonly Meter _meter;
	private readonly MeterListener _instrumentListener;
	private readonly List<Instrument> _instruments = new();
	private readonly TestMeterListener<double> _doubleListener;
	private readonly TestMeterListener<long> _longListener;

	public ProcessMetricsTests()
	{
		_meter = new Meter(typeof(ProcessMetricsTests).FullName!);
		_instrumentListener = new MeterListener
		{
			InstrumentPublished = (instrument, _) =>
			{
				if (instrument.Meter == _meter)
				{
					_instruments.Add(instrument);
				}
			},
		};
		_instrumentListener.Start();
		_doubleListener = new TestMeterListener<double>(_meter);
		_longListener = new TestMeterListener<long>(_meter);

		var config = new Dictionary<MetricsConfiguration.ProcessTracker, bool>
		{
			[MetricsConfiguration.ProcessTracker.UpTime] = true,
			[MetricsConfiguration.ProcessTracker.DiskReadBytes] = true,
			[MetricsConfiguration.ProcessTracker.DiskWrittenBytes] = true,
			[MetricsConfiguration.ProcessTracker.DiskReadOps] = true,
			[MetricsConfiguration.ProcessTracker.DiskWrittenOps] = true,
		};

		var sut = new ProcessMetrics(_meter, TimeSpan.Zero, config);
		sut.CreateUptimeMetric();
		sut.CreateDiskIoMetric();
		sut.CreateDiskOperationMetric();

		_doubleListener.Observe();
		_longListener.Observe();
	}

	public void Dispose()
	{
		_instrumentListener.Dispose();
		_doubleListener.Dispose();
		_longListener.Dispose();
		_meter.Dispose();
	}

	[Fact]
	public void publishes_the_process_metric_contract()
	{
		Assert.Collection(
			_instruments.OrderBy(instrument => instrument.Name),
			instrument => AssertInstrument<ObservableCounter<long>>(
				instrument,
				OpenTelemetryMetricDefinitions.ProcessDiskIo),
			instrument => AssertInstrument<ObservableGauge<double>>(
				instrument,
				OpenTelemetryMetricDefinitions.ProcessUptime),
			instrument => AssertInstrument<ObservableCounter<long>>(
				instrument,
				MetricDefinitions.TrogonEventstoreProcessDiskOperationCount));
	}

	[Fact]
	public void exposes_only_the_retained_process_trackers()
	{
		Assert.Equal(
			new[]
			{
				MetricsConfiguration.ProcessTracker.UpTime,
				MetricsConfiguration.ProcessTracker.DiskReadBytes,
				MetricsConfiguration.ProcessTracker.DiskReadOps,
				MetricsConfiguration.ProcessTracker.DiskWrittenBytes,
				MetricsConfiguration.ProcessTracker.DiskWrittenOps,
			},
			Enum.GetValues<MetricsConfiguration.ProcessTracker>());
	}

	[Fact]
	public void process_uptime_is_in_seconds_without_point_attributes()
	{
		var measurement = Assert.Single(
			_doubleListener.RetrieveMeasurements(Key(OpenTelemetryMetricDefinitions.ProcessUptime)));

		Assert.True(measurement.Value > 0);
		Assert.Empty(measurement.Tags);
	}

	[Fact]
	public void process_disk_io_has_the_required_direction_attribute()
	{
		Assert.Collection(
			_longListener.RetrieveMeasurements(Key(OpenTelemetryMetricDefinitions.ProcessDiskIo)),
			measurement => AssertDirection(measurement, "read"),
			measurement => AssertDirection(measurement, "write"));
	}

	[Fact]
	public void process_disk_operations_have_the_required_direction_attribute()
	{
		Assert.Collection(
			_longListener.RetrieveMeasurements(Key(MetricDefinitions.TrogonEventstoreProcessDiskOperationCount)),
			measurement => AssertDirection(measurement, "read"),
			measurement => AssertDirection(measurement, "write"));
	}

	private static void AssertDirection(TestMeterListener<long>.TestMeasurement measurement, string direction)
	{
		Assert.True(measurement.Value >= 0);
		var tag = Assert.Single(measurement.Tags);
		Assert.Equal(AttributeNames.DiskIoDirection, tag.Key);
		Assert.Equal(direction, tag.Value);
	}

	private static void AssertInstrument<TInstrument>(Instrument instrument, MetricDefinition definition)
		where TInstrument : Instrument
	{
		Assert.IsType<TInstrument>(instrument);
		Assert.Equal(definition.Name, instrument.Name);
		Assert.Equal(definition.Unit, instrument.Unit);
		Assert.Equal(definition.Description, instrument.Description);
	}

	private static string Key(MetricDefinition definition) => definition.Name;
}
