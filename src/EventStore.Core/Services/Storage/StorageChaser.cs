using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Services.Monitoring.Stats;
using EventStore.Core.Services.Storage.EpochManager;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.LogRecords;
using System.Threading.Tasks;
using ILogger = Serilog.ILogger;
using EventStore.LogCommon;
using static System.Threading.Timeout;

namespace EventStore.Core.Services.Storage;

public abstract class StorageChaser {
	protected static readonly ILogger Log = Serilog.Log.ForContext<StorageChaser>();
}

public class StorageChaser<TStreamId> : StorageChaser, IMonitoredQueue,
	IHandle<SystemMessage.SystemInit>,
	IHandle<SystemMessage.SystemStart>,
	IHandle<SystemMessage.BecomeShuttingDown>,
	IThreadPoolWorkItem
{

	private static readonly int TicksPerMs = (int)(Stopwatch.Frequency / 1000);
	private static readonly int MinFlushDelay = 2 * TicksPerMs;
	private static readonly ManualResetEventSlim FlushSignal = new ManualResetEventSlim(false, 1);
	private static readonly TimeSpan FlushWaitTimeout = TimeSpan.FromMilliseconds(10);

	public string Name => _queueStats.Name;

	private readonly IPublisher _leaderBus;
	private readonly IReadOnlyCheckpoint _writerCheckpoint;
	private readonly ITransactionFileChaser _chaser;
	private readonly IIndexCommitterService<TStreamId> _indexCommitterService;
	private readonly IEpochManager _epochManager;

	private readonly CancellationToken _stopToken; // cached to avoid ObjectDisposedException
	private readonly CancellationTokenSource _stop;
	private volatile bool _systemStarted;

	private readonly QueueStatsCollector _queueStats;

	private readonly Stopwatch _watch = Stopwatch.StartNew();
	private long _flushDelay;
	private long _lastFlush;

	private readonly List<IPrepareLogRecord<TStreamId>> _transaction = [];
	private bool _commitsAfterEof;

	private readonly TaskCompletionSource<object> _tcs = new();

	public Task Task
	{
		get { return _tcs.Task; }
	}

	public StorageChaser(IPublisher leaderBus,
		IReadOnlyCheckpoint writerCheckpoint,
		ITransactionFileChaser chaser,
		IIndexCommitterService<TStreamId> indexCommitterService,
		IEpochManager epochManager,
		QueueStatsManager queueStatsManager)
	{
		Ensure.NotNull(leaderBus, "leaderBus");
		Ensure.NotNull(writerCheckpoint, "writerCheckpoint");
		Ensure.NotNull(chaser, "chaser");
		Ensure.NotNull(indexCommitterService, "indexCommitterService");
		Ensure.NotNull(epochManager, "epochManager");

		_leaderBus = leaderBus;
		_writerCheckpoint = writerCheckpoint;
		_chaser = chaser;
		_indexCommitterService = indexCommitterService;
		_epochManager = epochManager;
		_queueStats = queueStatsManager.CreateQueueStatsCollector("Storage Chaser");

		_flushDelay = 0;
		_lastFlush = _watch.ElapsedTicks;

		_stop = new CancellationTokenSource();
		_stopToken = _stop.Token;
	}

	public void Handle(SystemMessage.SystemInit message) =>
		ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);

	public void Handle(SystemMessage.SystemStart message) =>
		_systemStarted = true;

	async void IThreadPoolWorkItem.Execute()
	{
		try
		{
			_queueStats.Start();
			QueueMonitor.Default.Register(this);

			_writerCheckpoint.Flushed += OnWriterFlushed;

			_chaser.Open();

			// We rebuild index till the chaser position, because
			// everything else will be done by chaser as during replication
			// with no concurrency issues with writer, as writer before jumping
			// into leader mode and accepting writes will wait till chaser caught up.
			_indexCommitterService.Init(_chaser.Checkpoint.Read());
			_leaderBus.Publish(new SystemMessage.ServiceInitialized("StorageChaser"));

			while (!_stopToken.IsCancellationRequested)
			{
				if (_systemStarted)
					await ChaserIteration(_stopToken);
				else
					await Task.Delay(1, _stopToken);
			}
		}
		catch (Exception exc)
		{
			Log.Fatal(exc, "Error in StorageChaser. Terminating...");
			_queueStats.EnterIdle();
			_queueStats.ProcessingStarted<FaultedChaserState>(0);
			_tcs.TrySetException(exc);

			Application.Exit(ExitCode.Error, "Error in StorageChaser. Terminating...\nError: " + exc.Message);
			await Task.Delay(InfiniteTimeSpan, _stopToken)
				.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing |
				                ConfigureAwaitOptions.ContinueOnCapturedContext);

			_queueStats.ProcessingEnded(0);
		}
		finally
		{
			_queueStats.Stop();
			QueueMonitor.Default.Unregister(this);
		}

		_writerCheckpoint.Flushed -= OnWriterFlushed;
		_chaser.Close();
		_leaderBus.Publish(new SystemMessage.ServiceShutdown(Name));
	}

	private void OnWriterFlushed(long obj) =>
		FlushSignal.Set();

	private async ValueTask ChaserIteration(CancellationToken token)
	{
		_queueStats.EnterBusy();

		FlushSignal.Reset(); // Reset the flush signal just before a read to reduce pointless reads from [flush flush read] patterns.

		var result = _chaser.TryReadNext();

		if (result.Success)
		{
			_queueStats.ProcessingStarted(result.LogRecord.GetType(), 0);
			await ProcessLogRecord(result, token);
			_queueStats.ProcessingEnded(1);
		}

		var start = _watch.ElapsedTicks;
		if (!result.Success || start - _lastFlush >= _flushDelay + MinFlushDelay)
		{
			_queueStats.ProcessingStarted<ChaserCheckpointFlush>(0);
			// todo: histogram metric?
			_chaser.Flush();
			_queueStats.ProcessingEnded(1);

			var end = _watch.ElapsedTicks;
			_flushDelay = end - start;
			_lastFlush = end;
		}

		if (!result.Success)
		{
			_queueStats.EnterIdle();
			// todo: histogram metric?
			FlushSignal.Wait(FlushWaitTimeout, token);
		}
	}

	private async ValueTask ProcessLogRecord(SeqReadResult result, CancellationToken token)
	{
		switch (result.LogRecord.RecordType)
		{
			case LogRecordType.Stream:
			case LogRecordType.EventType:
			case LogRecordType.Prepare:
			{
				var record = (IPrepareLogRecord<TStreamId>)result.LogRecord;
				ProcessPrepareRecord(record, result.RecordPostPosition);
				break;
			}
			case LogRecordType.Commit:
			{
				_commitsAfterEof = !result.Eof;
				var record = (CommitLogRecord)result.LogRecord;
				ProcessCommitRecord(record, result.RecordPostPosition);
				break;
			}
			case LogRecordType.System:
			{
				var record = (ISystemLogRecord)result.LogRecord;
				await ProcessSystemRecord(record, token);
				break;
			}
			case LogRecordType.Partition:
			case LogRecordType.PartitionType:
				break;
			default:
				throw new ArgumentOutOfRangeException();
		}

		if (result.Eof && result.LogRecord.RecordType != LogRecordType.Commit && _commitsAfterEof)
		{
			_commitsAfterEof = false;
			_leaderBus.Publish(new StorageMessage.TfEofAtNonCommitRecord());
		}
	}

	private void ProcessPrepareRecord(IPrepareLogRecord<TStreamId> record, long postPosition)
	{
		if (_transaction.Count > 0 && _transaction[0].TransactionPosition != record.TransactionPosition)
			CommitPendingTransaction(_transaction, postPosition);

		if (record.Flags.HasAnyOf(PrepareFlags.IsCommitted))
		{
			if (record.Flags.HasAnyOf(PrepareFlags.Data | PrepareFlags.StreamDelete))
				_transaction.Add(record);

			if (!record.Flags.HasAnyOf(PrepareFlags.TransactionEnd)) return;

			CommitPendingTransaction(_transaction, postPosition);

			long firstEventNumber;
			long lastEventNumber;
			if (record.Flags.HasAnyOf(PrepareFlags.Data))
			{
				firstEventNumber = record.ExpectedVersion + 1 - record.TransactionOffset;
				lastEventNumber = record.ExpectedVersion + 1;
			}
			else
			{
				firstEventNumber = record.ExpectedVersion + 1;
				lastEventNumber = record.ExpectedVersion;
			}

			_leaderBus.Publish(new StorageMessage.CommitAck(record.CorrelationId,
				record.LogPosition,
				record.TransactionPosition,
				firstEventNumber,
				lastEventNumber));
		}
		else if (record.Flags.HasAnyOf(PrepareFlags.TransactionBegin | PrepareFlags.TransactionEnd | PrepareFlags.Data))
		{
			_leaderBus.Publish(
				new StorageMessage.PrepareAck(record.CorrelationId, record.LogPosition, record.Flags));
		}
	}

	private void ProcessCommitRecord(CommitLogRecord record, long postPosition)
	{
		CommitPendingTransaction(_transaction, postPosition);

		var firstEventNumber = record.FirstEventNumber;
		var lastEventNumber = _indexCommitterService.GetCommitLastEventNumber(record);
		_indexCommitterService.AddPendingCommit(record, postPosition);
		if (lastEventNumber == EventNumber.Invalid)
			lastEventNumber = record.FirstEventNumber - 1;
		_leaderBus.Publish(new StorageMessage.CommitAck(record.CorrelationId, record.LogPosition,
			record.TransactionPosition, firstEventNumber, lastEventNumber));
	}

	private ValueTask ProcessSystemRecord(ISystemLogRecord record, CancellationToken token)
	{
		CommitPendingTransaction(_transaction, record.LogPosition);

		if (record.SystemRecordType is not SystemRecordType.Epoch) return ValueTask.CompletedTask;

		// Epoch record is written to TF, but possibly is not added to EpochManager
		// as we could be in Follower/Clone mode. We try to add epoch to EpochManager
		// every time we encounter EpochRecord while chasing. CacheEpoch call is idempotent,
		// but does integrity checks.
		var epoch = record.GetEpochRecord();
		return _epochManager.CacheEpoch(epoch, token);
	}

	private void CommitPendingTransaction(List<IPrepareLogRecord<TStreamId>> transaction, long postPosition)
	{
		if (transaction.Count <= 0) return;

		_indexCommitterService.AddPendingPrepare(transaction.ToArray(), postPosition);
		_transaction.Clear();
	}

	public void Handle(SystemMessage.BecomeShuttingDown message)
	{
		if (_stop.IsCancellationRequested) return;

		_stop.Cancel();
		_stop.Dispose();
	}

	public QueueStats GetStatistics() =>
		_queueStats.GetStatistics(0);

	private class ChaserCheckpointFlush;

	private class FaultedChaserState;
}
