#nullable enable
using System.Collections.Generic;
using EventStore.Core.Metrics;
using EventStore.Core.Time;
using EventStore.Core.TransactionLog.LogRecords;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.TransactionLog.Chunks;

public class TFChunkTracker : ITransactionFileTracker
{
	private readonly LogicalChunkReadDistributionMetric _readDistribution;
	private readonly DurationMetric _readDurationMetric;
	private readonly CounterSubMetric _readBytes;
	private readonly CounterSubMetric _readEvents;

	public TFChunkTracker(
		LogicalChunkReadDistributionMetric readDistribution,
		DurationMetric readDurationMetric,
		CounterSubMetric readBytes,
		CounterSubMetric readEvents)
	{

		_readDistribution = readDistribution;
		_readDurationMetric = readDurationMetric;
		_readBytes = readBytes;
		_readEvents = readEvents;
	}

	public void OnRead(Instant start, ILogRecord record, ITransactionFileTracker.Source source)
	{
		_readDistribution.Record(record);
		_readDurationMetric.Record(
			start,
			new KeyValuePair<string, object>(TrogonAttributeNames.StorageSource, source switch
			{
				ITransactionFileTracker.Source.ChunkCache => "chunk_cache",
				ITransactionFileTracker.Source.FileSystem => "file_system",
				_ => "unknown",
			}));

		if (record is not PrepareLogRecord prepare)
		{
			return;
		}

		_readBytes.Add(prepare.Data.Length + prepare.Metadata.Length);
		_readEvents.Add(1);
	}

	public class NoOp : ITransactionFileTracker
	{
		public void OnRead(Instant start, ILogRecord record, ITransactionFileTracker.Source source)
		{
		}
	}
}
