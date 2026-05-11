using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Services.Transport.Tcp;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Replication.LeaderReplication;

public class WhenReplicaSubscribesWithNoCommonEpochs<TLogFormat, TStreamId>
	: WithReplicationServiceAndEpochManager<TLogFormat, TStreamId>
{
	private readonly Guid _replicaId = Guid.NewGuid();
	private TcpConnectionManager _replicaManager;
	public override async Task When(CancellationToken token = default)
	{
		await EpochManager.WriteNewEpoch(0, token);
		await Writer.Write(CreateLogRecord(0), token);
		await Writer.Write(CreateLogRecord(1), token);
		await Writer.Write(CreateLogRecord(2), token);
		await Writer.Write(CreateLogRecord(3), token);
		await Writer.Write(CreateLogRecord(4), token);
		await EpochManager.WriteNewEpoch(1, token);

		var epochs = new[] {
			new Epoch(1010, 1, Guid.NewGuid()),
			new Epoch(999, 2, Guid.NewGuid())
		};

		(_, _replicaManager) = await AddSubscription(_replicaId, true, epochs, 1010, token);
	}

	[Test]
	public void subscription_is_sent_a_replica_subscribed_message_from_start()
	{
		var message = GetTcpSendsFor(_replicaManager).Select(x => x.Message).First();

		Assert.IsInstanceOf<ReplicationMessage.ReplicaSubscribed>(message);
		var subscribed = (ReplicationMessage.ReplicaSubscribed)message;
		Assert.Zero(subscribed.SubscriptionPosition);
		Assert.AreEqual(_replicaId, subscribed.SubscriptionId);
		Assert.AreEqual(LeaderId, subscribed.LeaderId);
	}
}

public class WhenReplicaWithSameEpochsSubscribesFromLastEpochPosition<TLogFormat, TStreamId>
	: WithReplicationServiceAndEpochManager<TLogFormat, TStreamId>
{
	private readonly Guid _replicaId = Guid.NewGuid();
	private TcpConnectionManager _replicaManager;
	private EpochRecord _lastEpoch;

	public override async Task When(CancellationToken token = default)
	{
		await EpochManager.WriteNewEpoch(0, token);
		await Writer.Write(CreateLogRecord(0), token);
		await Writer.Write(CreateLogRecord(1), token);
		await Writer.Write(CreateLogRecord(2), token);
		await Writer.Write(CreateLogRecord(3), token);
		await Writer.Write(CreateLogRecord(4), token);
		await EpochManager.WriteNewEpoch(1, token);

		_lastEpoch = EpochManager.GetLastEpoch();
		var epochs = (await EpochManager.GetLastEpochs(10, token))
			.Select(e => new Epoch(e.EpochPosition, e.EpochNumber, e.EpochId)).ToArray();

		(_, _replicaManager) = await AddSubscription(_replicaId, true, epochs, _lastEpoch.EpochPosition, token);
	}

	[Test]
	public void subscription_is_sent_a_replica_subscribed_message_from_last_epoch_position()
	{
		var message = GetTcpSendsFor(_replicaManager).Select(x => x.Message).First();

		Assert.IsInstanceOf<ReplicationMessage.ReplicaSubscribed>(message);
		var subscribed = (ReplicationMessage.ReplicaSubscribed)message;
		Assert.AreEqual(_lastEpoch.EpochPosition, subscribed.SubscriptionPosition);
		Assert.AreEqual(_replicaId, subscribed.SubscriptionId);
		Assert.AreEqual(LeaderId, subscribed.LeaderId);
	}
}

public class WhenReplicaWithSameEpochsSubscribesFromPositionLessThanLastEpochPosition<TLogFormat, TStreamId>
	: WithReplicationServiceAndEpochManager<TLogFormat, TStreamId>
{
	private readonly Guid _replicaId = Guid.NewGuid();
	private TcpConnectionManager _replicaManager;
	private EpochRecord _lastEpoch;
	private long _subscribedPosition;

	public override async Task When(CancellationToken token = default)
	{
		await EpochManager.WriteNewEpoch(0, token);
		await Writer.Write(CreateLogRecord(0), token);
		await Writer.Write(CreateLogRecord(1), token);
		await Writer.Write(CreateLogRecord(2), token);
		await Writer.Write(CreateLogRecord(3), token);
		(_, _subscribedPosition) = await Writer.Write(CreateLogRecord(4), token);
		await EpochManager.WriteNewEpoch(1, token);

		_lastEpoch = EpochManager.GetLastEpoch();
		var epochs = (await EpochManager.GetLastEpochs(10, token))
			.Select(e => new Epoch(e.EpochPosition, e.EpochNumber, e.EpochId)).ToArray();

		(_, _replicaManager) = await AddSubscription(_replicaId, true, epochs, _subscribedPosition, token);
	}

	[Test]
	public void subscription_is_sent_a_replica_subscribed_message_from_requested_position()
	{
		var message = GetTcpSendsFor(_replicaManager).Select(x => x.Message).First();

		Assert.IsInstanceOf<ReplicationMessage.ReplicaSubscribed>(message);
		var subscribed = (ReplicationMessage.ReplicaSubscribed)message;
		Assert.AreEqual(_subscribedPosition, subscribed.SubscriptionPosition);
		Assert.AreEqual(_replicaId, subscribed.SubscriptionId);
		Assert.AreEqual(LeaderId, subscribed.LeaderId);
	}
}

public class WhenReplicaWithAdditionalEpochsSubscribesToPositionPastLeadersLastEpoch<TLogFormat, TStreamId>
	: WithReplicationServiceAndEpochManager<TLogFormat, TStreamId>
{
	private readonly Guid _replicaId = Guid.NewGuid();
	private TcpConnectionManager _replicaManager;
	private List<Epoch> _replicaEpochs;

	public override async Task When(CancellationToken token = default)
	{
		await EpochManager.WriteNewEpoch(0, token);
		await Writer.Write(CreateLogRecord(0), token);
		await Writer.Write(CreateLogRecord(1), token);
		await Writer.Write(CreateLogRecord(2), token);
		await Writer.Write(CreateLogRecord(3), token);
		await Writer.Write(CreateLogRecord(4), token);
		await EpochManager.WriteNewEpoch(1, token);
		await Writer.Write(CreateLogRecord(5), token);
		await Writer.Write(CreateLogRecord(6), token);
		var (_, lastWritePosition) = await Writer.Write(CreateLogRecord(7), token);
		await Writer.Flush(token);

		_replicaEpochs =
		[
			new Epoch(lastWritePosition + 2000, 4, Guid.NewGuid()),
			new Epoch(lastWritePosition + 1000, 3, Guid.NewGuid()),
			new Epoch(lastWritePosition, 2, Guid.NewGuid()),
		];
		_replicaEpochs.AddRange((await EpochManager.GetLastEpochs(10, token))
			.Select(e => new Epoch(e.EpochPosition, e.EpochNumber, e.EpochId)).ToList());

		(_, _replicaManager) = await AddSubscription(_replicaId, true, _replicaEpochs.ToArray(),
			lastWritePosition + 2000, token);
	}

	[Test]
	public void subscription_is_sent_replica_subscribed_message_for_epoch_after_common_epoch()
	{
		var message = GetTcpSendsFor(_replicaManager).Select(x => x.Message).First();

		Assert.IsInstanceOf<ReplicationMessage.ReplicaSubscribed>(message);
		var subscribed = (ReplicationMessage.ReplicaSubscribed)message;
		Assert.AreEqual(_replicaEpochs[2].EpochPosition, subscribed.SubscriptionPosition);
		Assert.AreEqual(_replicaId, subscribed.SubscriptionId);
		Assert.AreEqual(LeaderId, subscribed.LeaderId);
	}
}

public class WhenReplicaSubscribesWithEpochThatDoesntExistOnLeaderButIsBeforeLeadersLastEpoch<TLogFormat, TStreamId>
	: WithReplicationServiceAndEpochManager<TLogFormat, TStreamId>
{
	private readonly Guid _replicaId = Guid.NewGuid();
	private TcpConnectionManager _replicaManager;
	private List<Epoch> _replicaEpochs;

	public override async Task When(CancellationToken token = default)
	{
		await EpochManager.WriteNewEpoch(0, token);
		await Writer.Write(CreateLogRecord(0), token);
		await Writer.Write(CreateLogRecord(1), token);
		var (_, otherEpochLogPosition) = await Writer.Write(CreateLogRecord(2), token);
		await Writer.Write(CreateLogRecord(3), token);
		await Writer.Write(CreateLogRecord(4), token);
		await EpochManager.WriteNewEpoch(2, token);

		var firstEpoch = (await EpochManager.GetLastEpochs(10, token)).First(e => e.EpochNumber == 0);
		_replicaEpochs =
		[
			new Epoch(otherEpochLogPosition, 1, Guid.NewGuid()),
			new Epoch(firstEpoch.EpochPosition, firstEpoch.EpochNumber, firstEpoch.EpochId)
		];

		(_, _replicaManager) = await AddSubscription(_replicaId, true, _replicaEpochs.ToArray(),
		_replicaEpochs[1].EpochPosition, token);
	}

	[Test]
	public void subscription_is_sent_replica_subscribed_message_for_epoch_after_common_epoch()
	{
		var message = GetTcpSendsFor(_replicaManager).Select(x => x.Message).First();

		Assert.IsInstanceOf<ReplicationMessage.ReplicaSubscribed>(message);
		var subscribed = (ReplicationMessage.ReplicaSubscribed)message;
		Assert.AreEqual(_replicaEpochs[0].EpochPosition, subscribed.SubscriptionPosition);
		Assert.AreEqual(_replicaId, subscribed.SubscriptionId);
		Assert.AreEqual(LeaderId, subscribed.LeaderId);
	}
}

public class WhenReplicaSubscribesWithAdditionalEpochPastLeadersWriterCheckpoint<TLogFormat, TStreamId>
	: WithReplicationServiceAndEpochManager<TLogFormat, TStreamId>
{
	private readonly Guid _replicaId = Guid.NewGuid();
	private TcpConnectionManager _replicaManager;
	private List<Epoch> _replicaEpochs;

	public override async Task When(CancellationToken token = default)
	{
		await EpochManager.WriteNewEpoch(0, token);
		await Writer.Write(CreateLogRecord(0), token);
		await Writer.Write(CreateLogRecord(1), token);
		await Writer.Write(CreateLogRecord(2), token);
		await Writer.Write(CreateLogRecord(3), token);
		await Writer.Write(CreateLogRecord(4), token);
		await EpochManager.WriteNewEpoch(1, token);

		var subscribePosition = Writer.Position + 1000;
		_replicaEpochs = new List<Epoch> {
			new Epoch(subscribePosition, 2, Guid.NewGuid()),
		};
		_replicaEpochs.AddRange((await EpochManager.GetLastEpochs(10, token))
			.Select(e => new Epoch(e.EpochPosition, e.EpochNumber, e.EpochId)).ToList());

		(_, _replicaManager) =
		await AddSubscription(_replicaId, true, _replicaEpochs.ToArray(), subscribePosition, token);
	}

	[Test]
	public void subscription_is_sent_replica_subscribed_message_for_leaders_writer_checkpoint()
	{
		var message = GetTcpSendsFor(_replicaManager).Select(x => x.Message).First();

		Assert.IsInstanceOf<ReplicationMessage.ReplicaSubscribed>(message);
		var subscribed = (ReplicationMessage.ReplicaSubscribed)message;
		Assert.AreEqual(Writer.Position, subscribed.SubscriptionPosition);
		Assert.AreEqual(_replicaId, subscribed.SubscriptionId);
		Assert.AreEqual(LeaderId, subscribed.LeaderId);
	}
}

public class WhenReplicaSubscribesWithAdditionalEpochAndLeaderHasEpochAfterCommonEpoch<TLogFormat, TStreamId>
	: WithReplicationServiceAndEpochManager<TLogFormat, TStreamId>
{
	private readonly Guid _replicaId = Guid.NewGuid();
	private TcpConnectionManager _replicaManager;
	private List<Epoch> _replicaEpochs;

	public override async Task When(CancellationToken token = default)
	{
		await EpochManager.WriteNewEpoch(0, token);
		await Writer.Write(CreateLogRecord(0), token);
		await Writer.Write(CreateLogRecord(1), token);
		await Writer.Write(CreateLogRecord(2), token);
		await EpochManager.WriteNewEpoch(1, token);
		await Writer.Write(CreateLogRecord(3), token);
		await Writer.Write(CreateLogRecord(4), token);
		await EpochManager.WriteNewEpoch(4, token);

		var subscribePosition = Writer.Position + 1000;
		_replicaEpochs = [new Epoch(subscribePosition, 2, Guid.NewGuid())];
		_replicaEpochs.AddRange((await EpochManager.GetLastEpochs(10, token))
			.Where(e => e.EpochNumber < 4)
			.Select(e => new Epoch(e.EpochPosition, e.EpochNumber, e.EpochId)).ToList());

		(_, _replicaManager) = await AddSubscription(_replicaId, true, _replicaEpochs.ToArray(), subscribePosition, token);
	}

	[Test]
	public void subscription_is_sent_replica_subscribed_message_for_leaders_epoch_after_common_epoch()
	{
		var message = GetTcpSendsFor(_replicaManager).Select(x => x.Message).First();

		Assert.IsInstanceOf<ReplicationMessage.ReplicaSubscribed>(message);
		var subscribed = (ReplicationMessage.ReplicaSubscribed)message;
		Assert.AreEqual(EpochManager.GetLastEpoch().EpochPosition, subscribed.SubscriptionPosition);
		Assert.AreEqual(_replicaId, subscribed.SubscriptionId);
		Assert.AreEqual(LeaderId, subscribed.LeaderId);
	}
}

public class WhenReplicaSubscribesWithUncachedEpoch<TLogFormat, TStreamId>
	: WithReplicationServiceAndEpochManager<TLogFormat, TStreamId>
{
	private readonly Guid _replicaId = Guid.NewGuid();
	private TcpConnectionManager _replicaManager;
	private List<Epoch> _replicaEpochs;
	public override async Task When(CancellationToken token)
	{
		await EpochManager.WriteNewEpoch(0, token);
		await Writer.Write(CreateLogRecord(0), token);
		await EpochManager.WriteNewEpoch(1, token);

		// The EpochManager for these tests only caches 5 epochs
		_replicaEpochs = (await EpochManager.GetLastEpochs(2, token))
			.Select(e => new Epoch(e.EpochPosition, e.EpochNumber, e.EpochId)).ToList();

		await Writer.Write(CreateLogRecord(1), token);
		await EpochManager.WriteNewEpoch(2, token);
		await Writer.Write(CreateLogRecord(2), token);
		await EpochManager.WriteNewEpoch(3, token);
		await Writer.Write(CreateLogRecord(3), token);
		await EpochManager.WriteNewEpoch(4, token);
		await Writer.Write(CreateLogRecord(4), token);
		await EpochManager.WriteNewEpoch(5, token);
		await Writer.Write(CreateLogRecord(5), token);
		await EpochManager.WriteNewEpoch(6, token);

		(_, _replicaManager) = await AddSubscription(_replicaId, true, _replicaEpochs.ToArray(),
			_replicaEpochs[0].EpochPosition, token);
	}

	[Test]
	public void subscription_is_sent_a_replica_subscribed_message_common_epoch()
	{
		var message = GetTcpSendsFor(_replicaManager).Select(x => x.Message).First();

		Assert.IsInstanceOf<ReplicationMessage.ReplicaSubscribed>(message);
		var subscribed = (ReplicationMessage.ReplicaSubscribed)message;
		Assert.AreEqual(_replicaEpochs[0].EpochPosition, subscribed.SubscriptionPosition);
		Assert.AreEqual(_replicaId, subscribed.SubscriptionId);
		Assert.AreEqual(LeaderId, subscribed.LeaderId);
	}
}

public class WhenReplicaSubscribesWithUncachedEpochThatDoesNotExistOnLeader<TLogFormat, TStreamId>
	: WithReplicationServiceAndEpochManager<TLogFormat, TStreamId>
{
	private readonly Guid _replicaId = Guid.NewGuid();
	private TcpConnectionManager _replicaManager;
	private List<Epoch> _replicaEpochs;
	private EpochRecord[] _uncachedLeaderEpochs;

	public override async Task When(CancellationToken token = default)
	{
		// The EpochManager for these tests only caches 5 epochs
		// Epochs 2 and 3 don't exist
		await EpochManager.WriteNewEpoch(0, token);
		await Writer.Write(CreateLogRecord(0), token);
		await EpochManager.WriteNewEpoch(1, token);
		await Writer.Write(CreateLogRecord(1), token);

		_uncachedLeaderEpochs = (await EpochManager.GetLastEpochs(2, token)).ToArray();

		await EpochManager.WriteNewEpoch(4, token);
		await Writer.Write(CreateLogRecord(2), token);
		await EpochManager.WriteNewEpoch(5, token);
		await Writer.Write(CreateLogRecord(3), token);
		await EpochManager.WriteNewEpoch(6, token);
		await Writer.Write(CreateLogRecord(4), token);
		await EpochManager.WriteNewEpoch(7, token);
		await Writer.Write(CreateLogRecord(5), token);
		await EpochManager.WriteNewEpoch(8, token);

		_replicaEpochs =
		[
			new Epoch(_uncachedLeaderEpochs[0].EpochPosition + 8000, 3, Guid.NewGuid()),
			new Epoch(_uncachedLeaderEpochs[0].EpochPosition + 4000, 2, Guid.NewGuid()),
			new Epoch(_uncachedLeaderEpochs[0].EpochPosition, _uncachedLeaderEpochs[0].EpochNumber,
				_uncachedLeaderEpochs[0].EpochId),

			new Epoch(_uncachedLeaderEpochs[1].EpochPosition, _uncachedLeaderEpochs[1].EpochNumber,
				_uncachedLeaderEpochs[1].EpochId)
		];

		(_, _replicaManager) = await AddSubscription(
			_replicaId,
			true,
			_replicaEpochs.ToArray(),
			_replicaEpochs[0].EpochPosition,
			token);
	}

	[Test]
	public async Task subscription_is_sent_a_replica_subscribed_message_to_epoch_position_after_common_epoch()
	{
		var message = GetTcpSendsFor(_replicaManager).Select(x => x.Message).First();

		Assert.IsInstanceOf<ReplicationMessage.ReplicaSubscribed>(message);
		var subscribed = (ReplicationMessage.ReplicaSubscribed)message;
		Assert.AreEqual((await EpochManager.GetLastEpochs(5, CancellationToken.None))
			.First(x => x.EpochNumber == 4).EpochPosition, subscribed.SubscriptionPosition);
		Assert.AreEqual(_replicaId, subscribed.SubscriptionId);
		Assert.AreEqual(LeaderId, subscribed.LeaderId);
	}
}
