using System.Collections.Generic;
using EventStore.Core.Time;
using TrogonEventStore.SemanticConventions;

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
			new KeyValuePair<string, object>(TrogonAttributeNames.QueueName, queueName),
			new KeyValuePair<string, object>(TrogonAttributeNames.QueueMessageType, messageType));
	}

	public class NoOp : IQueueProcessingTracker
	{
		public Instant RecordNow(Instant start, string messageType) => Instant.Now;
	}
}
