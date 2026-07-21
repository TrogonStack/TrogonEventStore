using System.Collections.Generic;
using System.Diagnostics.Metrics;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.Metrics
{
	// A metric that tracks the statuses of multiple components.
	// We are only expecting to have a handful of components.
	public class StatusMetric
	{
		private readonly List<StatusSubMetric> _subMetrics = new();

		public StatusMetric(Meter meter, MetricDefinition definition)
		{
			definition.EnsureInstrumentKind(MetricInstrumentKind.UpDownCounter);
			meter.CreateObservableUpDownCounter(
				definition.Name,
				Observe,
				definition.Unit,
				definition.Description);
		}

		public void Add(StatusSubMetric subMetric)
		{
			lock (_subMetrics)
			{
				_subMetrics.Add(subMetric);
			}
		}

		private IEnumerable<Measurement<long>> Observe()
		{
			lock (_subMetrics)
			{
				foreach (var instance in _subMetrics)
				{
					foreach (var measurement in instance.Observe())
					{
						yield return measurement;
					}
				}
			}
		}
	}
}
