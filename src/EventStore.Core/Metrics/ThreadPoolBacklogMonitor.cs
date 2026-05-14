using System;
using System.Threading;
using EventStore.Core.Bus;
using EventStore.Core.Services.Monitoring.Stats;
using EventStore.Core.Time;

namespace EventStore.Core.Metrics;

public sealed class ThreadPoolBacklogMonitor : IMonitoredQueue, IThreadPoolWorkItem, IDisposable
{
	private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
	private const string QueueName = "ThreadPoolBacklog";

	private readonly QueueStatsCollector _queueStats;
	private readonly QueueTracker _tracker;
	private readonly Timer _timer;
	private readonly object _timerGate = new();
	private Instant _enqueuedAt;
	private bool _timerDisposed;
	private int _stopped;

	public ThreadPoolBacklogMonitor(QueueStatsManager queueStatsManager, QueueTrackers trackers)
	{
		_queueStats = queueStatsManager.CreateQueueStatsCollector(QueueName);
		_tracker = trackers.GetTrackerForQueue(QueueName);
		_timer = new Timer(_ => QueueWorkItem());
	}

	public string Name => _queueStats.Name;

	public void Start()
	{
		_queueStats.Start();
		QueueMonitor.Default.Register(this);
		QueueWorkItem();
	}

	public void Stop()
	{
		if (Interlocked.Exchange(ref _stopped, 1) != 0)
		{
			return;
		}

		lock (_timerGate)
		{
			if (!_timerDisposed)
			{
				_timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
			}
		}

		QueueMonitor.Default.Unregister(this);
		_queueStats.Stop();
	}

	public void Dispose()
	{
		Stop();
		lock (_timerGate)
		{
			if (_timerDisposed)
			{
				return;
			}

			_timerDisposed = true;
			_timer.Dispose();
		}
	}

	public void Execute()
	{
		if (Volatile.Read(ref _stopped) != 0)
		{
			return;
		}

		_queueStats.EnterBusy();
		try
		{
			var length = GetPendingWorkItemCount();
			_queueStats.ProcessingStarted<ThreadPoolBacklogSample>(length);
			_tracker.RecordQueueLength(length);
			_tracker.RecordMessageDequeued(_enqueuedAt);
			_queueStats.ProcessingEnded(1);
		}
		finally
		{
			_queueStats.EnterIdle();
		}

		lock (_timerGate)
		{
			if (Volatile.Read(ref _stopped) == 0 && !_timerDisposed)
			{
				_timer.Change(SampleInterval, Timeout.InfiniteTimeSpan);
			}
		}
	}

	public QueueStats GetStatistics() =>
		_queueStats.GetStatistics(GetPendingWorkItemCount());

	private void QueueWorkItem()
	{
		if (Volatile.Read(ref _stopped) != 0)
		{
			return;
		}

		_enqueuedAt = _tracker.Now;
		ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
	}

	private static int GetPendingWorkItemCount()
	{
		var count = ThreadPool.PendingWorkItemCount;
		return count > int.MaxValue ? int.MaxValue : (int)count;
	}

	private sealed class ThreadPoolBacklogSample
	{
	}
}
