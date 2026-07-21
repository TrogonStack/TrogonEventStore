using System.Collections.Generic;
using System.Diagnostics.Metrics;
using EventStore.Core.Time;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.Metrics
{
	public class DurationMetric
	{
		private readonly Histogram<double> _histogram;
		private readonly IClock _clock;

		public DurationMetric(Meter meter, MetricDefinition definition, IClock clock = null)
		{
			definition.EnsureInstrumentKind(MetricInstrumentKind.Histogram);
			_clock = clock ?? Clock.Instance;
			_histogram = meter.CreateHistogram<double>(
				definition.Name,
				definition.Unit,
				definition.Description);
		}

		public Duration Start(string durationName) =>
			new(this, durationName, _clock.Now);

		public Instant Record(
			Instant start,
			KeyValuePair<string, object> tag1,
			KeyValuePair<string, object> tag2)
		{

			var now = _clock.Now;
			var elapsedSeconds = now.ElapsedSecondsSince(start);
			_histogram.Record(elapsedSeconds, tag1, tag2);
			return now;
		}

		public Instant Record(
			Instant start,
			KeyValuePair<string, object> tag1)
		{

			var now = _clock.Now;
			var elapsedSeconds = now.ElapsedSecondsSince(start);
			_histogram.Record(elapsedSeconds, tag1);
			return now;
		}
	}
}
