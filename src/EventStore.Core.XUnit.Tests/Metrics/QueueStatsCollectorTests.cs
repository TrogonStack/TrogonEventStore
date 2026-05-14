using EventStore.Core.Bus;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class QueueStatsCollectorTests
{
	[Fact]
	public void statistics_include_the_current_queue_length_in_the_peak()
	{
		var collector = new QueueStatsCollector("queue");
		collector.Start();
		try
		{
			var stats = collector.GetStatistics(currentQueueLength: 5);

			Assert.Equal(5, stats.Length);
			Assert.Equal(5, stats.LengthCurrentTryPeak);
			Assert.Equal(5, stats.LengthLifetimePeak);
		}
		finally
		{
			collector.Stop();
		}
	}

	[Fact]
	public void reported_queue_length_updates_the_peak()
	{
		var collector = new QueueStatsCollector("queue");
		collector.Start();
		try
		{
			collector.ReportQueueLength(7);
			var stats = collector.GetStatistics(currentQueueLength: 3);

			Assert.Equal(3, stats.Length);
			Assert.Equal(7, stats.LengthCurrentTryPeak);
			Assert.Equal(7, stats.LengthLifetimePeak);
		}
		finally
		{
			collector.Stop();
		}
	}
}
