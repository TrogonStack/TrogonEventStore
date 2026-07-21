using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using TrogonEventStore.SemanticConventions;
using Tag = System.Collections.Generic.KeyValuePair<string, object>;

namespace EventStore.Core.Metrics;

public class SummedCounterMetric
{
	private readonly object _lock = new();
	private readonly Func<string, Tag> _genTag;
	private readonly Dictionary<string, (List<Func<double>>, Tag[])> _subMetricGroups = new();

	public SummedCounterMetric(Meter meter, MetricDefinition definition, Func<string, Tag> genTag)
	{
		definition.EnsureInstrumentKind(MetricInstrumentKind.Counter);
		_genTag = genTag;
		meter.CreateObservableCounter(
			definition.Name,
			Observe,
			definition.Unit,
			definition.Description);
	}

	public void Register(string group, Func<double> subMetric)
	{
		lock (_lock)
		{
			if (_subMetricGroups.TryGetValue(group, out var pair))
			{
				pair.Item1.Add(subMetric);
			}
			else
			{
				var tags = new[] { _genTag(group) };
				_subMetricGroups[group] = (new() { subMetric }, tags);
			}
		}
	}

	private IEnumerable<Measurement<double>> Observe()
	{
		lock (_lock)
		{
			foreach (var (groupFuncs, groupTags) in _subMetricGroups.Values)
			{
				var total = 0d;
				foreach (var observe in groupFuncs)
				{
					total += observe();
				}

				yield return new(total, groupTags);
			}
		}
	}
}
