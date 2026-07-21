using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.Metrics
{
	// Use this class if the status can transition from one to another, as in a state machine
	// Use ActivityStatusMetric if the status represents an activity that starts and stops
	//
	// A closed state set keeps one-hot aggregation complete without unbounded cardinality.
	//
	// The component name and status are stored in tags
	// Multiple threads can SetStatus and Observe concurrently
	public class StatusSubMetric
	{
		private readonly object _lock = new();
		private readonly string _componentName;
		private readonly string[] _statuses;
		private string _status;

		public StatusSubMetric(
			string componentName,
			object initialStatus,
			StatusMetric metric,
			IEnumerable<string> statuses)
		{
			ArgumentNullException.ThrowIfNull(componentName);
			ArgumentNullException.ThrowIfNull(metric);
			ArgumentNullException.ThrowIfNull(statuses);
			_componentName = componentName.ToLowerInvariant();
			_statuses = new HashSet<string>(
				statuses.Select(NormalizeStatus).Append("unknown"),
				StringComparer.Ordinal).ToArray();
			_status = ResolveStatus(initialStatus?.ToString());

			metric.Add(this);
		}

		public void SetStatus(string status)
		{
			lock (_lock)
			{
				_status = ResolveStatus(status);
			}
		}

		public Measurement<long>[] Observe()
		{
			lock (_lock)
			{
				var measurements = new Measurement<long>[_statuses.Length];
				var index = 0;
				foreach (var status in _statuses)
				{
					measurements[index++] = new(
						status == _status ? 1 : 0,
						new KeyValuePair<string, object>(TrogonAttributeNames.ComponentName, _componentName),
						new KeyValuePair<string, object>(TrogonAttributeNames.ComponentStatus, status));
				}

				return measurements;
			}
		}

		private string ResolveStatus(string status)
		{
			var normalized = NormalizeStatus(status);
			return Array.IndexOf(_statuses, normalized) >= 0 ? normalized : "unknown";
		}

		private static string NormalizeStatus(string status) =>
			string.IsNullOrWhiteSpace(status) ? "unknown" : status.ToLowerInvariant();
	}
}
