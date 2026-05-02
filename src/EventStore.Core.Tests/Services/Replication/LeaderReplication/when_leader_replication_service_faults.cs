using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Services.Monitoring.Stats;
using EventStore.Core.Services.Replication;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Tests.Services.ElectionsService;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Replication.LeaderReplication;

[TestFixture]
public class when_leader_replication_service_faults : SpecificationWithDirectory
{
	[Test]
	public async Task publishes_service_shutdown_after_service_initialized()
	{
		var publisher = new SynchronousScheduler("publisher");
		var shutdowns = new ConcurrentQueue<SystemMessage.ServiceShutdown>();
		var initializations = new ConcurrentQueue<SystemMessage.ServiceInitialized>();
		var writerCheckpoint = new FaultingCheckpoint(Checkpoint.Writer) {
			ThrowOnFlushedSubscription = true
		};
		var dbConfig = CreateDbConfig(writerCheckpoint);
		var leaderId = Guid.NewGuid();

		publisher.Subscribe(new AdHocHandler<SystemMessage.ServiceInitialized>(message =>
		{
			if (message.ServiceName == "Leader Replication Service")
				initializations.Enqueue(message);
		}));
		publisher.Subscribe(new AdHocHandler<SystemMessage.ServiceShutdown>(message =>
		{
			if (message.ServiceName == "Leader Replication Service")
				shutdowns.Enqueue(message);
		}));

		var db = new TFChunkDb(dbConfig);
		try
		{
			await db.Open();

			var service = new LeaderReplicationService(
				publisher,
				leaderId,
				db,
				new SynchronousScheduler("tcpSend"),
				new FakeEpochManager(),
				clusterSize: 3,
				unsafeAllowSurplusNodes: false,
				new QueueStatsManager());

			service.Handle(new SystemMessage.SystemStart());
			service.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));

			AssertEx.IsOrBecomesTrue(() => initializations.Count == 1, TimeSpan.FromSeconds(5));

			AssertEx.IsOrBecomesTrue(() => service.Task.IsFaulted, TimeSpan.FromSeconds(5));
			AssertEx.IsOrBecomesTrue(() => shutdowns.Count == 1, TimeSpan.FromSeconds(5));
		}
		finally
		{
			await db.DisposeAsync();
		}
	}

	private TFChunkDbConfig CreateDbConfig(ICheckpoint writerCheckpoint)
	{
		ICheckpoint chaserCheckpoint = new InMemoryCheckpoint(Checkpoint.Chaser);
		ICheckpoint epochCheckpoint = new InMemoryCheckpoint(Checkpoint.Epoch, initValue: -1);
		ICheckpoint proposalCheckpoint = new InMemoryCheckpoint(Checkpoint.Proposal, initValue: -1);
		ICheckpoint truncateCheckpoint = new InMemoryCheckpoint(Checkpoint.Truncate, initValue: -1);
		ICheckpoint replicationCheckpoint = new InMemoryCheckpoint(-1);
		ICheckpoint indexCheckpoint = new InMemoryCheckpoint(-1);
		ICheckpoint streamExistenceFilterCheckpoint = new InMemoryCheckpoint(-1);

		return new TFChunkDbConfig(
			PathName,
			new VersionedPatternFileNamingStrategy(PathName, "chunk-"),
			chunkSize: 1000,
			maxChunksCacheSize: 10000,
			writerCheckpoint,
			chaserCheckpoint,
			epochCheckpoint,
			proposalCheckpoint,
			truncateCheckpoint,
			replicationCheckpoint,
			indexCheckpoint,
			streamExistenceFilterCheckpoint);
	}

	private sealed class FaultingCheckpoint(string name, long initialValue = 0) : ICheckpoint
	{
		private long _last = initialValue;
		private long _lastFlushed = initialValue;
		private Action<long> _flushed;

		public bool ThrowOnFlushedSubscription { get; set; }
		public string Name => name;

		public long Read()
		{
			return _lastFlushed;
		}

		public long ReadNonFlushed() => _last;

		public void Write(long checkpoint)
		{
			_last = checkpoint;
		}

		public void Flush()
		{
			if (_last == _lastFlushed)
				return;

			_lastFlushed = _last;
			_flushed?.Invoke(_lastFlushed);
		}

		public event Action<long> Flushed
		{
			add
			{
				if (ThrowOnFlushedSubscription)
					throw new InvalidOperationException("writer checkpoint subscription failed");

				_flushed += value;
			}
			remove
			{
				_flushed -= value;
			}
		}

		public void Close(bool flush)
		{
			if (flush)
				Flush();
		}
	}
}
