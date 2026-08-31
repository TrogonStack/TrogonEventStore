using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EndPoint = System.Net.EndPoint;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.Services.Replication;

public enum ReplicaPromotability
{
	NonPromotable,
	Promotable
}

public sealed record GrpcReplicaConnectionEndpoints
{
	public GrpcReplicaConnectionEndpoints(EndPoint leaderEndPoint, EndPoint advertisedReplicaEndPoint)
	{
		Ensure.NotNull(leaderEndPoint, nameof(leaderEndPoint));
		Ensure.NotNull(advertisedReplicaEndPoint, nameof(advertisedReplicaEndPoint));
		LeaderEndPoint = leaderEndPoint;
		AdvertisedReplicaEndPoint = advertisedReplicaEndPoint;
	}

	public EndPoint LeaderEndPoint { get; }
	public EndPoint AdvertisedReplicaEndPoint { get; }
}

public interface IGrpcReplicaService : IAsyncHandle<ReplicationMessage.SubscribeToLeader>,
	IHandle<ReplicationMessage.AckLogPosition>
{
	Task Task { get; }
	Task Start();
	ValueTask StopAsync();
}

public interface IGrpcReplicaServiceFactory
{
	IGrpcReplicaService Create(IPublisher publisher, GrpcReplicaConnectionEndpoints endpoints);
}

public sealed class GrpcReplicaServiceFactory : IGrpcReplicaServiceFactory
{
	private const int RequestQueueCapacity = 2;

	private readonly IReplicationGrpcClientFactory _clientFactory;
	private readonly IReplicaSubscriptionDataSource _dataSource;
	private readonly Guid _replicaInstanceId;
	private readonly ReplicaPromotability _promotability;

	public GrpcReplicaServiceFactory(
		IReplicationGrpcClientFactory clientFactory,
		IReplicaSubscriptionDataSource dataSource,
		Guid replicaInstanceId,
		ReplicaPromotability promotability)
	{
		_clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
		_dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
		Ensure.NotEmptyGuid(replicaInstanceId, nameof(replicaInstanceId));
		if (!Enum.IsDefined(promotability))
		{
			throw new ArgumentOutOfRangeException(nameof(promotability));
		}

		_replicaInstanceId = replicaInstanceId;
		_promotability = promotability;
	}

	public IGrpcReplicaService Create(IPublisher publisher, GrpcReplicaConnectionEndpoints endpoints)
	{
		Ensure.NotNull(publisher, nameof(publisher));
		Ensure.NotNull(endpoints, nameof(endpoints));

		return new GrpcReplicaService(
			publisher,
			_clientFactory.Create(endpoints.LeaderEndPoint),
			_dataSource,
			_replicaInstanceId,
			endpoints.LeaderEndPoint,
			endpoints.AdvertisedReplicaEndPoint,
			_promotability,
			RequestQueueCapacity);
	}
}

public sealed class GrpcReplicaServiceSupervisor :
	IAsyncHandle<SystemMessage.StateChangeMessage>,
	IAsyncHandle<ReplicationMessage.ReconnectToLeader>,
	IAsyncHandle<ReplicationMessage.SubscribeToLeader>,
	IHandle<ReplicationMessage.AckLogPosition>,
	IAsyncDisposable
{
	private static readonly ILogger Log = Serilog.Log.ForContext<GrpcReplicaServiceSupervisor>();

	private readonly IPublisher _publisher;
	private readonly IGrpcReplicaServiceFactory _factory;
	private readonly EndPoint _advertisedReplicaEndPoint;
	private readonly Action<Task> _trackTask;
	private readonly SemaphoreSlim _lifecycle = new(1, 1);
	private readonly object _activeLock = new();

	private ActiveStream _active;
	private MemberInfo _leader;
	private Guid _stateCorrelationId;
	private Guid _leaderConnectionCorrelationId;
	private VNodeState _state = VNodeState.Initializing;
	private bool _disposed;

	public GrpcReplicaServiceSupervisor(
		IPublisher publisher,
		IGrpcReplicaServiceFactory factory,
		EndPoint advertisedReplicaEndPoint,
		Action<Task> trackTask)
	{
		_publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
		_factory = factory ?? throw new ArgumentNullException(nameof(factory));
		_advertisedReplicaEndPoint = advertisedReplicaEndPoint ??
			throw new ArgumentNullException(nameof(advertisedReplicaEndPoint));
		_trackTask = trackTask ?? throw new ArgumentNullException(nameof(trackTask));
	}

	public async ValueTask HandleAsync(SystemMessage.StateChangeMessage message, CancellationToken cancellationToken)
	{
		await _lifecycle.WaitAsync(cancellationToken);
		try
		{
			if (_disposed)
			{
				return;
			}

			_state = message.State;
			switch (message)
			{
				case SystemMessage.BecomePreReplica preReplica:
					_stateCorrelationId = preReplica.CorrelationId;
					_leaderConnectionCorrelationId = preReplica.LeaderConnectionCorrelationId;
					_leader = preReplica.Leader;
					await ReplaceActiveAsync(preReplica.Leader, preReplica.LeaderConnectionCorrelationId,
						cancellationToken);
					break;
				case SystemMessage.BecomePreReadOnlyReplica preReadOnlyReplica:
					_stateCorrelationId = preReadOnlyReplica.CorrelationId;
					_leaderConnectionCorrelationId = preReadOnlyReplica.LeaderConnectionCorrelationId;
					_leader = preReadOnlyReplica.Leader;
					await ReplaceActiveAsync(preReadOnlyReplica.Leader,
						preReadOnlyReplica.LeaderConnectionCorrelationId, cancellationToken);
					break;
				case SystemMessage.ReplicaStateMessage replicaState
					when message.State is VNodeState.CatchingUp or VNodeState.Clone or VNodeState.Follower
						or VNodeState.ReadOnlyReplica:
					_leader = replicaState.Leader;
					break;
				default:
					_leader = null;
					await StopActiveAsync();
					break;
			}
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	public async ValueTask HandleAsync(
		ReplicationMessage.ReconnectToLeader message,
		CancellationToken cancellationToken)
	{
		await _lifecycle.WaitAsync(cancellationToken);
		try
		{
			if (_disposed)
			{
				return;
			}

			if (_state is not VNodeState.PreReplica and not VNodeState.PreReadOnlyReplica)
			{
				return;
			}

			_leader = message.Leader;
			_leaderConnectionCorrelationId = message.ConnectionCorrelationId;
			await ReplaceActiveAsync(message.Leader, message.ConnectionCorrelationId, cancellationToken);
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	public async ValueTask HandleAsync(
		ReplicationMessage.SubscribeToLeader message,
		CancellationToken cancellationToken)
	{
		await _lifecycle.WaitAsync(cancellationToken);
		try
		{
			if (_disposed)
			{
				return;
			}

			if (_state is not VNodeState.PreReplica and not VNodeState.PreReadOnlyReplica ||
				_leader is null ||
				message.StateCorrelationId != _stateCorrelationId ||
				message.LeaderId != _leader.InstanceId)
			{
				return;
			}

			var active = GetActive();
			if (active is null || active.SubscriptionStarted || active.Service.Task.IsCompleted)
			{
				await ReplaceActiveAsync(_leader, _leaderConnectionCorrelationId, cancellationToken);
				active = GetActive();
			}

			if (active is null)
			{
				return;
			}

			active.SubscriptionStarted = true;
			await active.Service.HandleAsync(message, cancellationToken);
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	public void Handle(ReplicationMessage.AckLogPosition message)
	{
		lock (_activeLock)
		{
			_active?.Service.Handle(message);
		}
	}

	public async ValueTask DisposeAsync()
	{
		await _lifecycle.WaitAsync();
		try
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			await StopActiveAsync();
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	private async ValueTask ReplaceActiveAsync(
		MemberInfo leader,
		Guid leaderConnectionCorrelationId,
		CancellationToken cancellationToken)
	{
		await StopActiveAsync();
		cancellationToken.ThrowIfCancellationRequested();

		var active = new ActiveStream();
		try
		{
			active.Service = _factory.Create(
				new FencedPublisher(this, active),
				new GrpcReplicaConnectionEndpoints(leader.HttpEndPoint, _advertisedReplicaEndPoint));
			SetActive(active);
			var task = active.Service.Start();
			_trackTask(task);
			if (task.IsFaulted || task.IsCanceled)
			{
				await task;
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			ClearActive(active);
			await StopAsync(active.Service);
			throw;
		}
		catch (Exception exception)
		{
			ClearActive(active);
			await StopAsync(active.Service);
			Log.Warning(exception, "Failed to start replication stream to [{leaderEndPoint}].", leader.HttpEndPoint);
			_publisher.Publish(new ReplicationMessage.LeaderConnectionFailed(
				leaderConnectionCorrelationId, leader));
		}
	}

	private async ValueTask StopActiveAsync()
	{
		var active = TakeActive();
		if (active is not null)
		{
			await StopAsync(active.Service);
		}
	}

	private static ValueTask StopAsync(IGrpcReplicaService service) =>
		service is null ? ValueTask.CompletedTask : service.StopAsync();

	private ActiveStream GetActive()
	{
		lock (_activeLock)
		{
			return _active;
		}
	}

	private void SetActive(ActiveStream active)
	{
		lock (_activeLock)
		{
			_active = active;
		}
	}

	private ActiveStream TakeActive()
	{
		lock (_activeLock)
		{
			var active = _active;
			_active = null;
			return active;
		}
	}

	private void ClearActive(ActiveStream active)
	{
		lock (_activeLock)
		{
			if (ReferenceEquals(_active, active))
			{
				_active = null;
			}
		}
	}

	private void PublishIfActive(ActiveStream active, Message message)
	{
		lock (_activeLock)
		{
			if (ReferenceEquals(_active, active))
			{
				_publisher.Publish(message);
			}
		}
	}

	private sealed class ActiveStream
	{
		public IGrpcReplicaService Service { get; set; }
		public bool SubscriptionStarted { get; set; }
	}

	private sealed class FencedPublisher(GrpcReplicaServiceSupervisor supervisor, ActiveStream active) : IPublisher
	{
		public void Publish(Message message) => supervisor.PublishIfActive(active, message);
	}
}
