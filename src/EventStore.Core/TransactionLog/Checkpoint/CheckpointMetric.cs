using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using EventStore.Core.Metrics;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.TransactionLog.Checkpoint
{
	// For now reading 'nonflushed' because the flushed values may give the impression
	// of the checkpoints being further behind than they really are.
	public class CheckpointMetric
	{
		private readonly IReadOnlyCheckpoint[] _checkpoints;
		private readonly Measurement<long>[] _measurements;
		private readonly KeyValuePair<string, object>[][] _tagss;

		public CheckpointMetric(
			Meter meter,
			MetricDefinition definition,
			params IReadOnlyCheckpoint[] checkpoints)
		{
			definition.EnsureInstrumentKind(MetricInstrumentKind.Gauge);
			_checkpoints = checkpoints;
			_measurements = new Measurement<long>[checkpoints.Length];
			_tagss = new KeyValuePair<string, object>[checkpoints.Length][];

			for (var i = 0; i < checkpoints.Length; i++)
			{
				_tagss[i] = new KeyValuePair<string, object>[] {
					new(TrogonAttributeNames.CheckpointName, checkpoints[i].Name),
					new(TrogonAttributeNames.CheckpointReadKind, "non_flushed"),
				};
			}

			meter.CreateObservableGauge(
				definition.Name,
				Observe,
				definition.Unit,
				definition.Description);
		}

		private IEnumerable<Measurement<long>> Observe()
		{
			for (var i = 0; i < _checkpoints.Length; i++)
			{
				// looks like the Measurement constructor will allocate an array for the tags but the
				// other constructor overloads appear to allocate two
				_measurements[i] = new Measurement<long>(
					value: _checkpoints[i].ReadNonFlushed(),
					tags: _tagss[i].AsSpan());
			}

			return _measurements;
		}
	}
}
