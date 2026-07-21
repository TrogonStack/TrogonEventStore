#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime;
using EventStore.Core.Util;
using TrogonEventStore.SemanticConventions;

using static EventStore.Common.Configuration.MetricsConfiguration;

namespace EventStore.Core.Metrics;

public class SystemMetrics
{
	private readonly Meter _meter;
	private readonly TimeSpan _timeout;
	private readonly Dictionary<SystemTracker, bool> _config;

	public SystemMetrics(Meter meter, TimeSpan timeout, Dictionary<SystemTracker, bool> config)
	{
		_meter = meter;
		_timeout = timeout;
		_config = config;
	}

	public void CreateLoadAverageMetric()
	{
		if (RuntimeInformation.IsWindows)
		{
			return;
		}

		var oneMinuteEnabled = IsEnabled(SystemTracker.LoadAverage1m);
		var fiveMinutesEnabled = IsEnabled(SystemTracker.LoadAverage5m);
		var fifteenMinutesEnabled = IsEnabled(SystemTracker.LoadAverage15m);
		if (!oneMinuteEnabled && !fiveMinutesEnabled && !fifteenMinutesEnabled)
		{
			return;
		}

		var getLoadAverages = Functions.Debounce(RuntimeStats.GetCpuLoadAverages, _timeout);
		CreateObservableGauge(
			MetricDefinitions.TrogonEventstoreSystemLoadAverage,
			() => ObserveLoadAverage(
				getLoadAverages(),
				oneMinuteEnabled,
				fiveMinutesEnabled,
				fifteenMinutesEnabled));
	}

	public void CreateFileSystemMetrics(string dbPath)
	{
		var usageEnabled = IsEnabled(SystemTracker.DriveUsedBytes);
		var limitEnabled = IsEnabled(SystemTracker.DriveTotalBytes);
		if (!usageEnabled && !limitEnabled)
		{
			return;
		}

		var getDriveInfo = Functions.Debounce(() => DriveStats.GetDriveInfo(dbPath), _timeout);
		if (usageEnabled)
		{
			CreateObservableUpDownCounter(
				OpenTelemetryMetricDefinitions.SystemFilesystemUsage,
				() => ObserveFileSystemUsage(getDriveInfo()));
		}

		if (limitEnabled)
		{
			CreateObservableUpDownCounter(
				OpenTelemetryMetricDefinitions.SystemFilesystemLimit,
				() => ObserveFileSystemLimit(getDriveInfo()));
		}
	}

	private static IEnumerable<Measurement<double>> ObserveLoadAverage(
		(double OneMinute, double FiveMinutes, double FifteenMinutes) loadAverage,
		bool oneMinuteEnabled,
		bool fiveMinutesEnabled,
		bool fifteenMinutesEnabled)
	{
		if (oneMinuteEnabled)
		{
			yield return new(
				loadAverage.OneMinute,
				new KeyValuePair<string, object?>(TrogonAttributeNames.SystemLoadAveragePeriod, "1m"));
		}

		if (fiveMinutesEnabled)
		{
			yield return new(
				loadAverage.FiveMinutes,
				new KeyValuePair<string, object?>(TrogonAttributeNames.SystemLoadAveragePeriod, "5m"));
		}

		if (fifteenMinutesEnabled)
		{
			yield return new(
				loadAverage.FifteenMinutes,
				new KeyValuePair<string, object?>(TrogonAttributeNames.SystemLoadAveragePeriod, "15m"));
		}
	}

	private static IEnumerable<Measurement<long>> ObserveFileSystemUsage(DriveData drive)
	{
		yield return new(
			drive.UsedBytes,
			new KeyValuePair<string, object?>(AttributeNames.SystemFilesystemState, "used"),
			new KeyValuePair<string, object?>(AttributeNames.SystemFilesystemMountpoint, drive.DiskName));
		yield return new(
			drive.AvailableBytes,
			new KeyValuePair<string, object?>(AttributeNames.SystemFilesystemState, "free"),
			new KeyValuePair<string, object?>(AttributeNames.SystemFilesystemMountpoint, drive.DiskName));
	}

	private static IEnumerable<Measurement<long>> ObserveFileSystemLimit(DriveData drive)
	{
		yield return new(
			drive.TotalBytes,
			new KeyValuePair<string, object?>(AttributeNames.SystemFilesystemMountpoint, drive.DiskName));
	}

	private void CreateObservableGauge(
		MetricDefinition definition,
		Func<IEnumerable<Measurement<double>>> observe)
	{
		EnsureKind(definition, MetricInstrumentKind.Gauge);
		_meter.CreateObservableGauge(
			definition.Name,
			observe,
			definition.Unit,
			definition.Description);
	}

	private void CreateObservableUpDownCounter(
		MetricDefinition definition,
		Func<IEnumerable<Measurement<long>>> observe)
	{
		EnsureKind(definition, MetricInstrumentKind.UpDownCounter);
		_meter.CreateObservableUpDownCounter(
			definition.Name,
			observe,
			definition.Unit,
			definition.Description);
	}

	private bool IsEnabled(SystemTracker tracker) =>
		_config.TryGetValue(tracker, out var enabled) && enabled;

	private static void EnsureKind(MetricDefinition definition, MetricInstrumentKind expected)
	{
		if (definition.InstrumentKind != expected)
		{
			throw new ArgumentException(
				$"Metric '{definition.Name}' requires a {expected} instrument.",
				nameof(definition));
		}
	}
}
