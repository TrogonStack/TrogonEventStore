#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using EventStore.Core.Util;
using TrogonEventStore.SemanticConventions;

using static EventStore.Common.Configuration.MetricsConfiguration;

namespace EventStore.Core.Metrics;

public class ProcessMetrics
{
	private readonly Meter _meter;
	private readonly Dictionary<ProcessTracker, bool> _config;
	private readonly Func<DiskIoData> _getDiskIo;
	private readonly DateTime _processStartTimeUtc;

	public ProcessMetrics(
		Meter meter,
		TimeSpan timeout,
		Dictionary<ProcessTracker, bool> config)
	{
		_meter = meter;
		_config = config;
		_getDiskIo = Functions.Debounce(ProcessStats.GetDiskIo, timeout);
		using var process = Process.GetCurrentProcess();
		_processStartTimeUtc = process.StartTime.ToUniversalTime();
	}

	public void CreateUptimeMetric()
	{
		if (!IsEnabled(ProcessTracker.UpTime))
		{
			return;
		}

		CreateObservableGauge(
			OpenTelemetryMetricDefinitions.ProcessUptime,
			() => (DateTime.UtcNow - _processStartTimeUtc).TotalSeconds);
	}

	public void CreateDiskIoMetric()
	{
		var readEnabled = IsEnabled(ProcessTracker.DiskReadBytes);
		var writeEnabled = IsEnabled(ProcessTracker.DiskWrittenBytes);
		if (!readEnabled && !writeEnabled)
		{
			return;
		}

		CreateObservableCounter(
			OpenTelemetryMetricDefinitions.ProcessDiskIo,
			() => ObserveDiskIo(readEnabled, writeEnabled));
	}

	public void CreateDiskOperationMetric()
	{
		var readEnabled = IsEnabled(ProcessTracker.DiskReadOps);
		var writeEnabled = IsEnabled(ProcessTracker.DiskWrittenOps);
		if (!readEnabled && !writeEnabled)
		{
			return;
		}

		CreateObservableCounter(
			MetricDefinitions.TrogonEventstoreProcessDiskOperationCount,
			() => ObserveDiskOperations(readEnabled, writeEnabled));
	}

	private IEnumerable<Measurement<long>> ObserveDiskIo(bool readEnabled, bool writeEnabled)
	{
		var diskIo = _getDiskIo();
		if (readEnabled)
		{
			yield return new(
				(long)diskIo.ReadBytes,
				new KeyValuePair<string, object?>(AttributeNames.DiskIoDirection, "read"));
		}

		if (writeEnabled)
		{
			yield return new(
				(long)diskIo.WrittenBytes,
				new KeyValuePair<string, object?>(AttributeNames.DiskIoDirection, "write"));
		}
	}

	private IEnumerable<Measurement<long>> ObserveDiskOperations(bool readEnabled, bool writeEnabled)
	{
		var diskIo = _getDiskIo();
		if (readEnabled)
		{
			yield return new(
				(long)diskIo.ReadOps,
				new KeyValuePair<string, object?>(AttributeNames.DiskIoDirection, "read"));
		}

		if (writeEnabled)
		{
			yield return new(
				(long)diskIo.WriteOps,
				new KeyValuePair<string, object?>(AttributeNames.DiskIoDirection, "write"));
		}
	}

	private void CreateObservableGauge(MetricDefinition definition, Func<double> observe)
	{
		EnsureKind(definition, MetricInstrumentKind.Gauge);
		_meter.CreateObservableGauge(
			definition.Name,
			observe,
			definition.Unit,
			definition.Description);
	}

	private void CreateObservableCounter(
		MetricDefinition definition,
		Func<IEnumerable<Measurement<long>>> observe)
	{
		EnsureKind(definition, MetricInstrumentKind.Counter);
		_meter.CreateObservableCounter(
			definition.Name,
			observe,
			definition.Unit,
			definition.Description);
	}

	private bool IsEnabled(ProcessTracker tracker) =>
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
