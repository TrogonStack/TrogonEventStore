using System;
using System.Collections.Generic;
using System.Linq;
using EventStore.Core.Data;
using EventStore.Core.TransactionLog.Scavenging;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class TestScavengeBackend : IScavengeStateBackend<string>
{
	public TestScavengeBackend()
	{
		var checkpointStore = new TestScavengeMap<Unit, ScavengeCheckpoint>();

		ChunkTimeStampRanges = new TestScavengeMap<int, ChunkTimeStampRange>();
		CollisionStorage = new TestScavengeMap<string, Unit>();
		Hashes = new TestScavengeMap<ulong, string>();
		MetaStorage = new TestMetastreamScavengeMap<ulong>();
		MetaCollisionStorage = new TestMetastreamScavengeMap<string>();
		OriginalStorage = new TestOriginalStreamScavengeMap<ulong>();
		OriginalCollisionStorage = new TestOriginalStreamScavengeMap<string>();
		CheckpointStorage = checkpointStore;
		ChunkWeights = new TestChunkWeightScavengeMap();
		TransactionManager = new TransactionManager<int>(new TestTransactionFactory(), checkpointStore);
	}

	public IScavengeMap<int, ChunkTimeStampRange> ChunkTimeStampRanges { get; }
	public IScavengeMap<string, Unit> CollisionStorage { get; }
	public IScavengeMap<ulong, string> Hashes { get; }
	public IMetastreamScavengeMap<ulong> MetaStorage { get; }
	public IMetastreamScavengeMap<string> MetaCollisionStorage { get; }
	public IOriginalStreamScavengeMap<ulong> OriginalStorage { get; }
	public IOriginalStreamScavengeMap<string> OriginalCollisionStorage { get; }
	public IScavengeMap<Unit, ScavengeCheckpoint> CheckpointStorage { get; }
	public IChunkWeightScavengeMap ChunkWeights { get; }
	public ITransactionManager TransactionManager { get; }

	public void Dispose()
	{
	}

	public void LogStats()
	{
	}
}

public class TestScavengeMap<TKey, TValue> : IScavengeMap<TKey, TValue>
{
	private readonly Dictionary<TKey, TValue> _records = new();

	public TValue this[TKey key]
	{
		set => _records[key] = value;
	}

	public bool TryGetValue(TKey key, out TValue value) => _records.TryGetValue(key, out value);

	public IEnumerable<KeyValuePair<TKey, TValue>> AllRecords() =>
		_records
			.ToDictionary(x => x.Key, x => x.Value)
			.OrderBy(x => x.Key);

	public bool TryRemove(TKey key, out TValue value)
	{
		_records.TryGetValue(key, out value);
		return _records.Remove(key);
	}
}

public class TestMetastreamScavengeMap<TKey> :
	TestScavengeMap<TKey, MetastreamData>,
	IMetastreamScavengeMap<TKey>
{
	public void SetTombstone(TKey key)
	{
		if (!TryGetValue(key, out var existing))
			existing = new MetastreamData();

		this[key] = new MetastreamData(
			isTombstoned: true,
			discardPoint: existing.DiscardPoint);
	}

	public void SetDiscardPoint(TKey key, DiscardPoint discardPoint)
	{
		if (!TryGetValue(key, out var existing))
			existing = new MetastreamData();

		this[key] = new MetastreamData(
			isTombstoned: existing.IsTombstoned,
			discardPoint: discardPoint);
	}

	public void DeleteAll()
	{
		foreach (var record in AllRecords())
			TryRemove(record.Key, out _);
	}
}

public class TestOriginalStreamScavengeMap<TKey> :
	TestScavengeMap<TKey, OriginalStreamData>,
	IOriginalStreamScavengeMap<TKey>
{
	public void SetTombstone(TKey key)
	{
		if (!TryGetValue(key, out var existing))
			existing = new OriginalStreamData();

		this[key] = new OriginalStreamData
		{
			DiscardPoint = existing.DiscardPoint,
			MaybeDiscardPoint = existing.MaybeDiscardPoint,
			MaxAge = existing.MaxAge,
			MaxCount = existing.MaxCount,
			TruncateBefore = existing.TruncateBefore,
			Status = CalculationStatus.Active,
			IsTombstoned = true,
		};
	}

	public void SetMetadata(TKey key, StreamMetadata metadata)
	{
		if (!TryGetValue(key, out var existing))
			existing = new OriginalStreamData();

		this[key] = new OriginalStreamData
		{
			MaybeDiscardPoint = existing.MaybeDiscardPoint,
			DiscardPoint = existing.DiscardPoint,
			IsTombstoned = existing.IsTombstoned,
			Status = CalculationStatus.Active,
			MaxAge = metadata.MaxAge,
			MaxCount = metadata.MaxCount,
			TruncateBefore = metadata.TruncateBefore,
		};
	}

	public void SetDiscardPoints(
		TKey key,
		CalculationStatus status,
		DiscardPoint discardPoint,
		DiscardPoint maybeDiscardPoint)
	{
		if (!TryGetValue(key, out var existing))
			throw new Exception("Missing original stream scavenge data for test key.");

		this[key] = new OriginalStreamData
		{
			IsTombstoned = existing.IsTombstoned,
			MaxAge = existing.MaxAge,
			MaxCount = existing.MaxCount,
			TruncateBefore = existing.TruncateBefore,
			Status = status,
			DiscardPoint = discardPoint,
			MaybeDiscardPoint = maybeDiscardPoint,
		};
	}

	public bool TryGetChunkExecutionInfo(TKey key, out ChunkExecutionInfo info)
	{
		if (!TryGetValue(key, out var data))
		{
			info = default;
			return false;
		}

		info = new ChunkExecutionInfo(
			isTombstoned: data.IsTombstoned,
			discardPoint: data.DiscardPoint,
			maybeDiscardPoint: data.MaybeDiscardPoint,
			maxAge: data.MaxAge);

		return true;
	}

	public IEnumerable<KeyValuePair<TKey, OriginalStreamData>> ActiveRecords() =>
		AllRecords().Where(static x => x.Value.Status == CalculationStatus.Active);

	public IEnumerable<KeyValuePair<TKey, OriginalStreamData>> ActiveRecordsFromCheckpoint(TKey checkpoint) =>
		ActiveRecords().SkipWhile(x => Comparer<TKey>.Default.Compare(x.Key, checkpoint) <= 0);

	public void DeleteMany(bool deleteArchived)
	{
		foreach (var record in AllRecords())
		{
			if (record.Value.Status == CalculationStatus.Spent ||
			    record.Value.Status == CalculationStatus.Archived && deleteArchived)
			{
				TryRemove(record.Key, out _);
			}
		}
	}
}

public class TestChunkWeightScavengeMap :
	TestScavengeMap<int, float>,
	IChunkWeightScavengeMap
{
	public bool AllWeightsAreZero()
	{
		foreach (var record in AllRecords())
		{
			if (record.Value != 0)
				return false;
		}

		return true;
	}

	public void IncreaseWeight(int logicalChunkNumber, float extraWeight)
	{
		if (!TryGetValue(logicalChunkNumber, out var weight))
			weight = 0;

		this[logicalChunkNumber] = weight + extraWeight;
	}

	public void ResetChunkWeights(int startLogicalChunkNumber, int endLogicalChunkNumber)
	{
		for (var i = startLogicalChunkNumber; i <= endLogicalChunkNumber; i++)
			TryRemove(i, out _);
	}

	public float SumChunkWeights(int startLogicalChunkNumber, int endLogicalChunkNumber)
	{
		var totalWeight = 0f;

		for (var i = startLogicalChunkNumber; i <= endLogicalChunkNumber; i++)
		{
			if (TryGetValue(i, out var weight))
				totalWeight += weight;
		}

		return totalWeight;
	}
}

public class TestTransactionFactory : ITransactionFactory<int>
{
	private int _transactionNumber;

	public int Begin() => _transactionNumber++;

	public void Commit(int transaction)
	{
	}

	public void Rollback(int transaction)
	{
	}
}
