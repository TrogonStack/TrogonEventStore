using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;

namespace EventStore.Core.Metrics;

public interface IQueueLengthTracker
{
	void SetQueueLength(int length);
}

public class QueueLengthTracker : IQueueLengthTracker
{
	private readonly KeyValuePair<string, object>[] _tags;
	private int _length;

	public QueueLengthTracker(ObservableUpDownMetric<int> metric, string queueName)
	{
		_tags = [new("queue", queueName)];
		metric.Register(() => new Measurement<int>(Volatile.Read(ref _length), _tags.AsSpan()));
	}

	public void SetQueueLength(int length)
	{
		Volatile.Write(ref _length, length);
	}

	public class NoOp : IQueueLengthTracker
	{
		public void SetQueueLength(int length)
		{
		}
	}
}
