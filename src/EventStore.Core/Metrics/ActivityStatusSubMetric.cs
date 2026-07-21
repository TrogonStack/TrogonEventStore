using System;
using System.Linq;

namespace EventStore.Core.Metrics
{
	// When there is one activity at a time and it is disposed before starting the next.
	// It would be possible to drive a version of this from ActivitySource/ActivityListener
	// for cases where we are already incurring the cost of allocating the Activities
	public class ActivityStatusSubMetric : StatusSubMetric, IDisposable
	{
		private const string Idle = "Idle";

		public ActivityStatusSubMetric(string componentName, StatusMetric metric, params string[] activities)
			: base(componentName, Idle, metric, activities.Prepend(Idle))
		{
		}

		public IDisposable StartActivity(string name)
		{
			SetStatus(name);
			return this;
		}

		public void Dispose()
		{
			SetStatus(Idle);
		}
	}
}
