using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using EventStore.Core.Caching;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.Metrics;

public class CacheResourcesMetrics
{
	private readonly ObservableUpDownMetric<long> _bytesMetric;
	private readonly ObservableUpDownMetric<long> _entriesMetric;

	public CacheResourcesMetrics(
		Meter meter,
		MetricDefinition bytesMetricDefinition,
		MetricDefinition entriesMetricDefinition)
	{
		_bytesMetric = new ObservableUpDownMetric<long>(meter, bytesMetricDefinition);
		_entriesMetric = new ObservableUpDownMetric<long>(meter, entriesMetricDefinition);
	}

	public void Register(string cache, ResizerUnit unit, Func<CacheStats> getStats)
	{
		var sizeAndCapacityMetric = unit == ResizerUnit.Entries
			? _entriesMetric
			: _bytesMetric;

		Register(sizeAndCapacityMetric, cache, "capacity", () => getStats().Capacity);
		Register(sizeAndCapacityMetric, cache, "size", () => getStats().Size);
		Register(_entriesMetric, cache, "count", () => getStats().Count);
	}

	private static void Register(
		ObservableUpDownMetric<long> metric,
		string cache,
		string metricName,
		Func<long> measurementProvider)
	{

		var tags = new KeyValuePair<string, object>[] {
			new(TrogonAttributeNames.CacheName, cache),
			new(TrogonAttributeNames.CacheResource, metricName)
		};
		metric.Register(() => new(measurementProvider(), tags));
	}
}
