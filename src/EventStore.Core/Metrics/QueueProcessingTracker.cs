using System.Collections.Generic;
using EventStore.Core.Time;

namespace EventStore.Core.Metrics;

public interface IQueueProcessingTracker
{
	Instant RecordNow(Instant start, string messageType);
}

public class QueueProcessingTracker(DurationMetric metric, string queueName) : IQueueProcessingTracker
{
	public Instant RecordNow(Instant start, string messageType)
	{
		return metric.Record(
			start: start,
			new KeyValuePair<string, object>("queue", queueName),
			new KeyValuePair<string, object>("message-type", messageType));
	}

	public class NoOp : IQueueProcessingTracker
	{
		public Instant RecordNow(Instant start, string messageType) => Instant.Now;
	}
}
