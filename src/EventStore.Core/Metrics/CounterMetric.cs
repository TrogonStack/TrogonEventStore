using System.Collections.Generic;
using System.Diagnostics.Metrics;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.Metrics
{
	public class CounterMetric
	{
		private readonly List<CounterSubMetric> _subMetrics = new();
		private readonly object _lock = new();

		public CounterMetric(Meter meter, MetricDefinition definition)
		{
			definition.EnsureInstrumentKind(MetricInstrumentKind.Counter);
			meter.CreateObservableCounter(
				definition.Name,
				Observe,
				definition.Unit,
				definition.Description);
		}

		public void Add(CounterSubMetric subMetric)
		{
			lock (_lock)
			{
				_subMetrics.Add(subMetric);
			}
		}

		private IEnumerable<Measurement<long>> Observe()
		{
			lock (_lock)
			{
				foreach (CounterSubMetric subMetric in _subMetrics)
				{
					yield return subMetric.Observe();
				}
			}
		}
	}
}
