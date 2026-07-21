using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime;
using EventStore.Common.Configuration;
using EventStore.Core.Metrics;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class SystemMetricsTests : IDisposable
{
	private readonly Meter _meter;
	private readonly MeterListener _instrumentListener;
	private readonly List<Instrument> _instruments = new();
	private readonly TestMeterListener<double> _doubleListener;
	private readonly TestMeterListener<long> _longListener;

	public SystemMetricsTests()
	{
		_meter = new Meter(typeof(SystemMetricsTests).FullName!);
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

		var config = new Dictionary<MetricsConfiguration.SystemTracker, bool>
		{
			[MetricsConfiguration.SystemTracker.LoadAverage1m] = true,
			[MetricsConfiguration.SystemTracker.LoadAverage5m] = true,
			[MetricsConfiguration.SystemTracker.LoadAverage15m] = true,
			[MetricsConfiguration.SystemTracker.DriveUsedBytes] = true,
			[MetricsConfiguration.SystemTracker.DriveTotalBytes] = true,
		};

		var sut = new SystemMetrics(_meter, TimeSpan.Zero, config);
		sut.CreateLoadAverageMetric();
		sut.CreateFileSystemMetrics(".");

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
	public void publishes_the_system_metric_contract()
	{
		var instruments = _instruments.OrderBy(instrument => instrument.Name).ToArray();
		if (RuntimeInformation.IsWindows)
		{
			Assert.Collection(
				instruments,
				instrument => AssertInstrument<ObservableUpDownCounter<long>>(
					instrument,
					OpenTelemetryMetricDefinitions.SystemFilesystemLimit),
				instrument => AssertInstrument<ObservableUpDownCounter<long>>(
					instrument,
					OpenTelemetryMetricDefinitions.SystemFilesystemUsage));
			return;
		}

		Assert.Collection(
			instruments,
			instrument => AssertInstrument<ObservableUpDownCounter<long>>(
				instrument,
				OpenTelemetryMetricDefinitions.SystemFilesystemLimit),
			instrument => AssertInstrument<ObservableUpDownCounter<long>>(
				instrument,
				OpenTelemetryMetricDefinitions.SystemFilesystemUsage),
			instrument => AssertInstrument<ObservableGauge<double>>(
				instrument,
				MetricDefinitions.TrogonEventstoreSystemLoadAverage));
	}

	[Fact]
	public void exposes_only_the_retained_system_trackers()
	{
		Assert.Equal(
			new[]
			{
				MetricsConfiguration.SystemTracker.LoadAverage1m,
				MetricsConfiguration.SystemTracker.LoadAverage5m,
				MetricsConfiguration.SystemTracker.LoadAverage15m,
				MetricsConfiguration.SystemTracker.DriveTotalBytes,
				MetricsConfiguration.SystemTracker.DriveUsedBytes,
			},
			Enum.GetValues<MetricsConfiguration.SystemTracker>());
	}

	[Fact]
	public void load_average_has_the_required_period_attribute()
	{
		if (RuntimeInformation.IsWindows)
		{
			return;
		}

		Assert.Collection(
			_doubleListener.RetrieveMeasurements(Key(MetricDefinitions.TrogonEventstoreSystemLoadAverage)),
			measurement => AssertPeriod(measurement, "1m"),
			measurement => AssertPeriod(measurement, "5m"),
			measurement => AssertPeriod(measurement, "15m"));
	}

	[Fact]
	public void filesystem_usage_has_state_and_mountpoint_attributes()
	{
		Assert.Collection(
			_longListener.RetrieveMeasurements(Key(OpenTelemetryMetricDefinitions.SystemFilesystemUsage)),
			measurement => AssertFileSystemUsage(measurement, "used"),
			measurement => AssertFileSystemUsage(measurement, "free"));
	}

	[Fact]
	public void filesystem_usage_states_sum_to_limit()
	{
		var usage = _longListener
			.RetrieveMeasurements(Key(OpenTelemetryMetricDefinitions.SystemFilesystemUsage))
			.Sum(measurement => measurement.Value);
		var limit = Assert.Single(
			_longListener.RetrieveMeasurements(Key(OpenTelemetryMetricDefinitions.SystemFilesystemLimit)));

		Assert.Equal(limit.Value, usage);
	}

	[Fact]
	public void filesystem_limit_has_mountpoint_attribute()
	{
		var measurement = Assert.Single(
			_longListener.RetrieveMeasurements(Key(OpenTelemetryMetricDefinitions.SystemFilesystemLimit)));

		Assert.True(measurement.Value > 0);
		var tag = Assert.Single(measurement.Tags);
		AssertNonEmptyTag(tag, AttributeNames.SystemFilesystemMountpoint);
	}

	private static void AssertPeriod(TestMeterListener<double>.TestMeasurement measurement, string period)
	{
		Assert.True(measurement.Value >= 0);
		var tag = Assert.Single(measurement.Tags);
		AssertTag(tag, TrogonAttributeNames.SystemLoadAveragePeriod, period);
	}

	private static void AssertFileSystemUsage(
		TestMeterListener<long>.TestMeasurement measurement,
		string state)
	{
		Assert.True(measurement.Value >= 0);
		Assert.Collection(
			measurement.Tags,
			tag => AssertTag(tag, AttributeNames.SystemFilesystemState, state),
			tag => AssertNonEmptyTag(tag, AttributeNames.SystemFilesystemMountpoint));
	}

	private static void AssertNonEmptyTag(KeyValuePair<string, object> tag, string name)
	{
		Assert.Equal(name, tag.Key);
		Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(tag.Value)));
	}

	private static void AssertTag(KeyValuePair<string, object> tag, string name, string value)
	{
		Assert.Equal(name, tag.Key);
		Assert.Equal(value, tag.Value);
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
