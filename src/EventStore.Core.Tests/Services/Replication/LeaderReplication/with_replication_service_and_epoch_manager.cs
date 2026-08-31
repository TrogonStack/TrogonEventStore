using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotNext;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.LogAbstraction;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Core.Services.Replication;
using EventStore.Core.Services.Storage.EpochManager;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Replication.LeaderReplication;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public abstract class
	WithReplicationServiceAndEpochManager<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	protected int ClusterSize = 3;
	protected SynchronousScheduler Publisher = new("publisher");
	protected LeaderReplicationService Service;
	protected LogFormatAbstractor<TStreamId> _logFormat;
	protected Guid LeaderId = Guid.NewGuid();

	protected TFChunkDbConfig DbConfig;
	protected EpochManager<TStreamId> EpochManager;
	protected TFChunkDb Db;
	protected TFChunkWriter Writer;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();

		var indexDirectory = GetFilePathFor("index");
		_logFormat =
			LogFormatHelper<TLogFormat, TStreamId>.LogFormatFactory.Create(new() { IndexDirectory = indexDirectory, });

		DbConfig = CreateDbConfig();
		Db = new TFChunkDb(DbConfig);
		await Db.Open();

		Writer = new TFChunkWriter(Db);
		Writer.Open();

		EpochManager = new EpochManager<TStreamId>(
			Publisher,
			5,
			DbConfig.EpochCheckpoint,
			Writer,
			1, 1,
			() => new TFChunkReader(Db, Db.Config.WriterCheckpoint),
			_logFormat.RecordFactory,
			_logFormat.StreamNameIndex,
			_logFormat.EventTypeIndex,
			_logFormat.CreatePartitionManager(
				reader: new TFChunkReader(Db, Db.Config.WriterCheckpoint),
				writer: Writer),
			Guid.NewGuid());
		Service = new LeaderReplicationService(
			Publisher,
			LeaderId,
			Db,
			EpochManager,
			ClusterSize,
			false,
			new QueueStatsManager());

		Service.Handle(new SystemMessage.SystemStart());
		Service.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));

		await When();
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		_logFormat?.Dispose();
		await base.TestFixtureTearDown();
		Service.Handle(new SystemMessage.BecomeShuttingDown(Guid.NewGuid(), true, true));
	}

	public IPrepareLogRecord<TStreamId> CreateLogRecord(long eventNumber, string data = "*************")
	{
		var tStreamId = LogFormatHelper<TLogFormat, TStreamId>.StreamId;
		var eventType = LogFormatHelper<TLogFormat, TStreamId>.EventTypeId;
		return LogRecord.Prepare(_logFormat.RecordFactory, Writer.Position, Guid.NewGuid(), Guid.NewGuid(), 0, 0,
			tStreamId, eventNumber, PrepareFlags.None, eventType, Encoding.UTF8.GetBytes(data),
			null, DateTime.UtcNow);
	}

	public async ValueTask<(Guid, TestReplicationSession)> AddSubscription(Guid replicaId, bool isPromotable,
		Epoch[] epochs, long logPosition, CancellationToken token = default)
	{
		var session = new TestReplicationSession(replicaId);
		var subRequest = new ReplicationMessage.ReplicaSubscriptionRequest(
			Guid.NewGuid(),
			new NoopEnvelope(),
			session,
			ReplicationSubscriptionVersions.V_CURRENT,
			logPosition,
			Guid.NewGuid(),
			epochs,
			PortsHelper.GetLoopback(),
			LeaderId,
			replicaId,
			isPromotable);
		await Service.As<IAsyncHandle<ReplicationMessage.ReplicaSubscriptionRequest>>()
			.HandleAsync(subRequest, token);
		return (session.ConnectionId, session);
	}

	public abstract Task When(CancellationToken token = default);

	private TFChunkDbConfig CreateDbConfig()
	{
		ICheckpoint writerChk = new InMemoryCheckpoint(Checkpoint.Writer);
		ICheckpoint chaserChk = new InMemoryCheckpoint(Checkpoint.Chaser);
		ICheckpoint epochChk = new InMemoryCheckpoint(Checkpoint.Epoch, initValue: -1);
		ICheckpoint proposalChk = new InMemoryCheckpoint(Checkpoint.Proposal, initValue: -1);
		ICheckpoint truncateChk = new InMemoryCheckpoint(Checkpoint.Truncate, initValue: -1);
		ICheckpoint replicationCheckpoint = new InMemoryCheckpoint(-1);
		ICheckpoint indexCheckpoint = new InMemoryCheckpoint(-1);
		ICheckpoint streamExistenceFilterCheckpoint = new InMemoryCheckpoint(-1);
		var nodeConfig = new TFChunkDbConfig(
			PathName,
			new VersionedPatternFileNamingStrategy(PathName, "chunk-"),
			chunkSize: 1000,
			maxChunksCacheSize: 10000,
			writerChk,
			chaserChk,
			epochChk,
			proposalChk,
			truncateChk,
			replicationCheckpoint,
			indexCheckpoint,
			streamExistenceFilterCheckpoint);
		return nodeConfig;
	}
}
