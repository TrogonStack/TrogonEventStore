using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DotNext;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Core.Services.Replication;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Tests.Services.ElectionsService;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.Util;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Replication.LeaderReplication;

public abstract class WithReplicationService : SpecificationWithDirectoryPerTestFixture
{
	protected string EventStreamId = "test_stream";
	protected int ClusterSize = 3;
	protected SynchronousScheduler Publisher = new("publisher");
	protected LeaderReplicationService Service;

	protected ConcurrentQueue<ReplicationTrackingMessage.ReplicaWriteAck> ReplicaWriteAcks = new();

	protected ConcurrentQueue<SystemMessage.VNodeConnectionLost> ReplicaLostMessages = new();

	protected Guid LeaderId = Guid.NewGuid();
	protected Guid ReplicaId = Guid.NewGuid();
	protected Guid ReplicaId2 = Guid.NewGuid();
	protected Guid ReadOnlyReplicaId = Guid.NewGuid();
	protected Guid ReplicaIdV0 = Guid.NewGuid();

	protected Guid ReplicaSubscriptionId;
	protected Guid ReplicaSubscriptionId2;
	protected Guid ReadOnlyReplicaSubscriptionId;
	protected Guid ReplicaSubscriptionIdV0;

	protected TestReplicationSession ReplicaSession1;
	protected TestReplicationSession ReplicaSession2;
	protected TestReplicationSession ReadOnlyReplicaSession;
	protected TestReplicationSession ReplicaSessionV0;

	protected TFChunkDbConfig DbConfig;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		Publisher.Subscribe(
			new AdHocHandler<ReplicationTrackingMessage.ReplicaWriteAck>(msg => ReplicaWriteAcks.Enqueue(msg)));
		Publisher.Subscribe(
			new AdHocHandler<SystemMessage.VNodeConnectionLost>(msg => ReplicaLostMessages.Enqueue(msg)));
		DbConfig = CreateDbConfig();
		var db = new TFChunkDb(DbConfig);
		await db.Open();
		Service = new LeaderReplicationService(
			publisher: Publisher,
			instanceId: LeaderId,
			db: db,
			epochManager: new FakeEpochManager(),
			clusterSize: ClusterSize,
			unsafeAllowSurplusNodes: false,
			queueStatsManager: new QueueStatsManager());

		Service.Handle(new SystemMessage.SystemStart());
		Service.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));

		(ReplicaSubscriptionId, ReplicaSession1) =
			await AddSubscription(ReplicaId, ReplicationSubscriptionVersions.V_CURRENT, true);
		(ReplicaSubscriptionId2, ReplicaSession2) =
			await AddSubscription(ReplicaId2, ReplicationSubscriptionVersions.V_CURRENT, true);
		(ReadOnlyReplicaSubscriptionId, ReadOnlyReplicaSession) =
			await AddSubscription(ReadOnlyReplicaId, ReplicationSubscriptionVersions.V_CURRENT, false);
		(ReplicaSubscriptionIdV0, ReplicaSessionV0) =
			await AddSubscription(ReplicaIdV0, ReplicationSubscriptionVersions.V0, true);

		When();
	}

	[OneTimeTearDown]
	public override async Task TestFixtureTearDown()
	{
		await base.TestFixtureTearDown();
		Service.Handle(new SystemMessage.BecomeShuttingDown(Guid.NewGuid(), true, true));
	}

	private async ValueTask<(Guid, TestReplicationSession)> AddSubscription(Guid replicaId, int version,
		bool isPromotable, CancellationToken token = default)
	{
		var session = new TestReplicationSession(replicaId);
		var subRequest = new ReplicationMessage.ReplicaSubscriptionRequest(
			Guid.NewGuid(),
			new NoopEnvelope(),
			session,
			version,
			0,
			Guid.NewGuid(),
			[],
			PortsHelper.GetLoopback(),
			LeaderId,
			replicaId,
			isPromotable);
		await Service.As<IAsyncHandle<ReplicationMessage.ReplicaSubscriptionRequest>>().HandleAsync(subRequest, token);
		return (session.ConnectionId, session);
	}

	public abstract void When();

	protected void BecomeLeader()
	{
		Service.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
	}

	protected void BecomeUnknown()
	{
		Service.Handle(new SystemMessage.BecomeUnknown(Guid.NewGuid()));
	}

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
			1000,
			10000,
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
