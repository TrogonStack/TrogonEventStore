using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Cluster.Settings;
using EventStore.Core.Messages;
using EventStore.Core.Services.Storage.EpochManager;
using EventStore.Core.Services.Transport.Grpc.Replication;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.LogRecords;
using EndPoint = System.Net.EndPoint;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.Services.Replication;

public interface IReplicaSubscriptionDataSource
{
	long ReadNonFlushed();
	ValueTask<Guid> GetCurrentChunkIdAsync(long logPosition, CancellationToken cancellationToken);
	ValueTask<IReadOnlyList<EpochRecord>> GetLastEpochsAsync(int maxCount, CancellationToken cancellationToken);
}

public sealed class ReplicaSubscriptionDataSource : IReplicaSubscriptionDataSource
{
	private readonly TFChunkDb _db;
	private readonly IEpochManager _epochManager;

	public ReplicaSubscriptionDataSource(TFChunkDb db, IEpochManager epochManager)
	{
		_db = db ?? throw new ArgumentNullException(nameof(db));
		_epochManager = epochManager ?? throw new ArgumentNullException(nameof(epochManager));
	}

	public long ReadNonFlushed() => _db.Config.WriterCheckpoint.ReadNonFlushed();

	public async ValueTask<Guid> GetCurrentChunkIdAsync(
		long logPosition,
		CancellationToken cancellationToken)
	{
		var chunk = await _db.Manager.TryGetChunkForAsync(logPosition, cancellationToken);
		return chunk?.ChunkHeader.ChunkId ?? Guid.Empty;
	}

	public ValueTask<IReadOnlyList<EpochRecord>> GetLastEpochsAsync(
		int maxCount,
		CancellationToken cancellationToken) =>
		_epochManager.GetLastEpochs(maxCount, cancellationToken);
}

public sealed class GrpcReplicaService : IGrpcReplicaService,
	IAsyncDisposable
{
	private static readonly ILogger Log = Serilog.Log.ForContext<GrpcReplicaService>();

	private readonly IPublisher _publisher;
	private readonly IReplicationGrpcClient _client;
	private readonly IReplicaSubscriptionDataSource _dataSource;
	private readonly Guid _replicaInstanceId;
	private readonly EndPoint _leaderEndPoint;
	private readonly EndPoint _advertisedGrpcEndPoint;
	private readonly ReplicaPromotability _promotability;
	private readonly Channel<RequestWorkItem> _requests;
	private readonly CancellationTokenSource _lifetime = new();
	private readonly object _ackLock = new();
	private readonly object _startLock = new();
	private readonly Guid _connectionId = Guid.NewGuid();

	private ReplicationMessage.AckLogPosition _pendingAcknowledgement;
	private Guid _subscriptionId;
	private long _latestReplicationLogPosition = long.MinValue;
	private long _latestWriterLogPosition = long.MinValue;
	private bool _acknowledgementQueued;
	private int _started;
	private int _subscriptionStarted;
	private int _expectedCancellation;
	private int _lossPublished;

	public GrpcReplicaService(
		IPublisher publisher,
		IReplicationGrpcClient client,
		IReplicaSubscriptionDataSource dataSource,
		Guid replicaInstanceId,
		EndPoint leaderEndPoint,
		EndPoint advertisedGrpcEndPoint,
		ReplicaPromotability promotability,
		int requestQueueCapacity)
	{
		Ensure.NotNull(publisher, nameof(publisher));
		Ensure.NotNull(client, nameof(client));
		Ensure.NotNull(dataSource, nameof(dataSource));
		Ensure.NotEmptyGuid(replicaInstanceId, nameof(replicaInstanceId));
		Ensure.NotNull(leaderEndPoint, nameof(leaderEndPoint));
		Ensure.NotNull(advertisedGrpcEndPoint, nameof(advertisedGrpcEndPoint));
		Ensure.Positive(requestQueueCapacity, nameof(requestQueueCapacity));
		if (!Enum.IsDefined(promotability))
		{
			throw new ArgumentOutOfRangeException(nameof(promotability));
		}

		_publisher = publisher;
		_client = client;
		_dataSource = dataSource;
		_replicaInstanceId = replicaInstanceId;
		_leaderEndPoint = leaderEndPoint;
		_advertisedGrpcEndPoint = advertisedGrpcEndPoint;
		_promotability = promotability;
		_requests = Channel.CreateBounded<RequestWorkItem>(new BoundedChannelOptions(requestQueueCapacity)
		{
			SingleReader = true,
			SingleWriter = false,
			FullMode = BoundedChannelFullMode.Wait
		});
	}

	public Guid ConnectionId => _connectionId;
	public Task Completion { get; private set; } = Task.CompletedTask;
	public Task Task => Completion;

	public Task Start()
	{
		lock (_startLock)
		{
			if (Interlocked.Exchange(ref _started, 1) != 0)
			{
				throw new InvalidOperationException("The replication stream has already been started.");
			}

			Completion = RunAsync();
			return Completion;
		}
	}

	public async ValueTask HandleAsync(
		ReplicationMessage.SubscribeToLeader message,
		CancellationToken cancellationToken)
	{
		if (Volatile.Read(ref _started) == 0)
		{
			throw new InvalidOperationException("The replication stream has not been started.");
		}

		if (Interlocked.Exchange(ref _subscriptionStarted, 1) != 0)
		{
			throw new InvalidOperationException("The replication subscription has already been started.");
		}

		var logPosition = _dataSource.ReadNonFlushed();
		var chunkId = await _dataSource.GetCurrentChunkIdAsync(logPosition, cancellationToken);
		var epochs = await _dataSource.GetLastEpochsAsync(
			ClusterConsts.SubscriptionLastEpochCount,
			cancellationToken);
		_subscriptionId = message.SubscriptionId;

		var subscribe = new ReplicationMessage.SubscribeReplica(
			ReplicationSubscriptionVersions.V_CURRENT,
			logPosition,
			chunkId,
			epochs,
			_advertisedGrpcEndPoint,
			message.LeaderId,
			message.SubscriptionId,
			_promotability == ReplicaPromotability.Promotable,
			_replicaInstanceId);
		await _requests.Writer.WriteAsync(
			new FrameWorkItem(ReplicationGrpcCodec.ToGrpc(subscribe)),
			cancellationToken);
	}

	public void Handle(ReplicationMessage.AckLogPosition message)
	{
		lock (_ackLock)
		{
			if (message.SubscriptionId != _subscriptionId ||
				message.ReplicationLogPosition < _latestReplicationLogPosition ||
				message.WriterLogPosition < _latestWriterLogPosition)
			{
				return;
			}

			_pendingAcknowledgement = message;
			_latestReplicationLogPosition = message.ReplicationLogPosition;
			_latestWriterLogPosition = message.WriterLogPosition;
			if (_acknowledgementQueued)
			{
				return;
			}

			_acknowledgementQueued = _requests.Writer.TryWrite(AcknowledgementWorkItem.Instance);
		}
	}

	public async ValueTask StopAsync()
	{
		Interlocked.Exchange(ref _expectedCancellation, 1);
		_requests.Writer.TryComplete();
		_lifetime.Cancel();
		await Completion;
	}

	public ValueTask DisposeAsync() => StopAsync();

	private async Task RunAsync()
	{
		IReplicationGrpcCall call = null;
		Task requestTask = null;
		Task responseTask = null;

		try
		{
			call = _client.Replicate(_lifetime.Token);
			requestTask = PumpRequestsAsync(call, _lifetime.Token);
			responseTask = ReadResponsesAsync(call, _lifetime.Token);

			var first = await Task.WhenAny(requestTask, responseTask);
			if (first == responseTask)
			{
				try
				{
					await responseTask;
				}
				finally
				{
					_requests.Writer.TryComplete();
				}

				await requestTask;
			}
			else
			{
				try
				{
					await requestTask;
				}
				finally
				{
					_lifetime.Cancel();
				}

				await responseTask;
			}
		}
		catch (Exception) when (Volatile.Read(ref _expectedCancellation) != 0)
		{
		}
		catch (Exception exception)
		{
			Log.Warning(exception, "Replication stream to [{leaderEndPoint}] ended unexpectedly.", _leaderEndPoint);
		}
		finally
		{
			_requests.Writer.TryComplete();
			_lifetime.Cancel();
			await ObserveAsync(requestTask);
			await ObserveAsync(responseTask);
			call?.Dispose();
			_client.Dispose();

			if (Volatile.Read(ref _expectedCancellation) == 0)
			{
				PublishConnectionLost();
			}
		}
	}

	private async Task PumpRequestsAsync(IReplicationGrpcCall call, CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var request in _requests.Reader.ReadAllAsync(cancellationToken))
			{
				switch (request)
				{
					case FrameWorkItem frame:
						await call.WriteAsync(frame.Value);
						break;
					case AcknowledgementWorkItem:
						var acknowledgement = TakePendingAcknowledgement();
						if (acknowledgement is not null)
						{
							await call.WriteAsync(ReplicationGrpcCodec.ToGrpc(acknowledgement));
						}

						break;
				}
			}
		}
		finally
		{
			if (!cancellationToken.IsCancellationRequested)
			{
				await call.CompleteRequestAsync();
			}
		}
	}

	private async Task ReadResponsesAsync(IReplicationGrpcCall call, CancellationToken cancellationToken)
	{
		var connectionEstablished = false;
		await foreach (var frame in call.ReadAllAsync(cancellationToken))
		{
			var message = ReplicationGrpcCodec.FromGrpc(frame);
			if (!connectionEstablished)
			{
				_publisher.Publish(new SystemMessage.VNodeConnectionEstablished(_leaderEndPoint, _connectionId));
				connectionEstablished = true;
			}

			if (message is ReplicationMessage.ReplicaSubscribed subscribed)
			{
				message = new ReplicationMessage.ReplicaSubscribed(
					subscribed.LeaderId,
					subscribed.SubscriptionId,
					subscribed.SubscriptionPosition,
					_leaderEndPoint);
			}

			_publisher.Publish(message);
		}
	}

	private ReplicationMessage.AckLogPosition TakePendingAcknowledgement()
	{
		lock (_ackLock)
		{
			var acknowledgement = _pendingAcknowledgement;
			_pendingAcknowledgement = null;
			_acknowledgementQueued = false;
			return acknowledgement;
		}
	}

	private void PublishConnectionLost()
	{
		if (Interlocked.Exchange(ref _lossPublished, 1) == 0)
		{
			_publisher.Publish(new SystemMessage.VNodeConnectionLost(_leaderEndPoint, _connectionId));
		}
	}

	private static async Task ObserveAsync(Task task)
	{
		if (task is null)
		{
			return;
		}

		try
		{
			await task;
		}
		catch
		{
		}
	}

	private abstract record RequestWorkItem;
	private sealed record FrameWorkItem(EventStore.Replication.ReplicaFrame Value) : RequestWorkItem;
	private sealed record AcknowledgementWorkItem : RequestWorkItem
	{
		public static readonly AcknowledgementWorkItem Instance = new();
	}
}
