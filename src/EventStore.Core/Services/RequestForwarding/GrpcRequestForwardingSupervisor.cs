using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Services.Transport.Grpc.Forwarding;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.Services.RequestForwarding;

public sealed class GrpcRequestForwardingSupervisor :
	IHandle<SystemMessage.StateChangeMessage>,
	IHandle<ReplicationMessage.ReconnectToLeader>,
	IHandle<ClientMessage.ForwardMessage>,
	IHandle<GrpcRequestForwardingMessage.Reconnect>,
	IHandle<GrpcRequestForwardingMessage.StreamClosed>,
	IAsyncDisposable
{
	private static readonly ILogger Log = Serilog.Log.ForContext<GrpcRequestForwardingSupervisor>();
	public static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromMilliseconds(500);

	private readonly object _sync = new();
	private readonly IPublisher _publisher;
	private readonly IGrpcRequestForwardingServiceFactory _factory;
	private readonly Action<Task> _trackTask;
	private readonly TimeSpan _reconnectDelay;

	private VNodeState _state = VNodeState.Initializing;
	private MemberInfo _leader;
	private ActiveStream _active;
	private long _connectionGeneration;
	private long _scheduledReconnectGeneration = -1;
	private bool _disposed;

	public GrpcRequestForwardingSupervisor(
		IPublisher publisher,
		IGrpcRequestForwardingServiceFactory factory,
		Action<Task> trackTask,
		TimeSpan reconnectDelay)
	{
		_publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
		_factory = factory ?? throw new ArgumentNullException(nameof(factory));
		_trackTask = trackTask ?? throw new ArgumentNullException(nameof(trackTask));
		if (reconnectDelay < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(reconnectDelay));
		}

		_reconnectDelay = reconnectDelay;
	}

	public void Handle(SystemMessage.StateChangeMessage message)
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_state = message.State;
			switch (message)
			{
				case SystemMessage.BecomePreReplica preReplica:
					ConnectToLeader(preReplica.Leader);
					break;
				case SystemMessage.BecomePreReadOnlyReplica preReadOnlyReplica:
					ConnectToLeader(preReadOnlyReplica.Leader);
					break;
				case SystemMessage.ReplicaStateMessage replicaState
					when message.State is VNodeState.CatchingUp or VNodeState.Clone or VNodeState.Follower
						or VNodeState.ReadOnlyReplica:
					_leader = replicaState.Leader;
					break;
				default:
					_leader = null;
					Disconnect();
					break;
			}
		}
	}

	public void Handle(ReplicationMessage.ReconnectToLeader message)
	{
		lock (_sync)
		{
			if (_disposed || !IsForwardingState(_state))
			{
				return;
			}

			if (HasHealthyStreamTo(message.Leader))
			{
				_leader = message.Leader;
				return;
			}

			ConnectToLeader(message.Leader);
		}
	}

	public void Handle(ClientMessage.ForwardMessage message)
	{
		if (message.Message is not ClientMessage.WriteRequestMessage request || !IsForwardableWrite(request))
		{
			throw new ArgumentException(
				$"{message.Message.GetType().Name} is not supported by gRPC request forwarding.",
				nameof(message));
		}

		lock (_sync)
		{
			if (_disposed || !IsForwardingState(_state))
			{
				return;
			}

			var active = _active;
			switch (active?.Service.TryForward(request))
			{
				case RequestForwardingAdmission.QueueFull:
					PublishIfActive(active, new ClientMessage.NotHandled(
						request.InternalCorrId,
						ClientMessage.NotHandled.Types.NotHandledReason.TooBusy,
						"Request forwarding queue is full."));
					break;
				case RequestForwardingAdmission.Closed:
					PublishIfActive(active, new ClientMessage.NotHandled(
						request.InternalCorrId,
						ClientMessage.NotHandled.Types.NotHandledReason.NotReady,
						"Request forwarding stream is closed."));
					break;
				case RequestForwardingAdmission.CredentialsRequireTls:
					PublishIfActive(active, new TcpMessage.NotAuthenticated(
						request.InternalCorrId,
						"Credentials cannot be forwarded unless transport security is enabled."));
					break;
			}
		}
	}

	public void Handle(GrpcRequestForwardingMessage.Reconnect message)
	{
		lock (_sync)
		{
			if (_disposed ||
				!IsForwardingState(_state) ||
				_leader is null ||
				message.LeaderId != _leader.InstanceId ||
				message.ConnectionGeneration != _connectionGeneration ||
				_active is not null)
			{
				return;
			}

			ConnectToLeader(_leader);
		}
	}

	public void Handle(GrpcRequestForwardingMessage.StreamClosed message)
	{
		lock (_sync)
		{
			if (_disposed ||
				message.ConnectionGeneration != _connectionGeneration ||
				_active?.ConnectionGeneration != message.ConnectionGeneration ||
				_active.LeaderId != message.LeaderId)
			{
				return;
			}

			_active = null;
			Log.Warning("Request forwarding stream to leader {leaderId:B} closed.", message.LeaderId);
			if (IsForwardingState(_state) && _leader?.InstanceId == message.LeaderId)
			{
				ScheduleReconnect(message.LeaderId, message.ConnectionGeneration);
			}
		}
	}

	public async ValueTask DisposeAsync()
	{
		Task completion;
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_state = VNodeState.Shutdown;
			_leader = null;
			completion = Disconnect();
		}

		await ObserveAsync(completion);
	}

	private void ConnectToLeader(MemberInfo leader)
	{
		_leader = leader;
		var connectionGeneration = ++_connectionGeneration;
		_scheduledReconnectGeneration = -1;
		var previous = _active;
		_active = null;
		previous?.Service.Stop();

		IGrpcRequestForwardingService service = null;
		try
		{
			var active = new ActiveStream(leader.InstanceId, leader.HttpEndPoint, connectionGeneration);
			service = _factory.Create(
				message => TryPublishIfActive(active, message),
				_publisher.Publish,
				leader.HttpEndPoint,
				new ForwardingSessionGeneration(connectionGeneration));
			active.Service = service;
			_active = active;

			var streamTask = service.Start();
			_trackTask(streamTask);
			var observer = ObserveCompletionAsync(active, streamTask);
			_trackTask(observer);
		}
		catch (Exception exception)
		{
			if (_active?.ConnectionGeneration == connectionGeneration)
			{
				_active = null;
			}

			service?.Stop();
			Log.Warning(exception, "Failed to start request forwarding stream to [{leaderEndPoint}].",
				leader.HttpEndPoint);
			if (connectionGeneration == _connectionGeneration)
			{
				ScheduleReconnect(leader.InstanceId, connectionGeneration);
			}
		}
	}

	private async Task ObserveCompletionAsync(ActiveStream active, Task streamTask)
	{
		await ObserveAsync(streamTask);
		_publisher.Publish(new GrpcRequestForwardingMessage.StreamClosed(
			active.LeaderId,
			active.ConnectionGeneration));
	}

	private void ScheduleReconnect(Guid leaderId, long connectionGeneration)
	{
		if (_scheduledReconnectGeneration == connectionGeneration)
		{
			return;
		}

		_scheduledReconnectGeneration = connectionGeneration;
		_publisher.Publish(TimerMessage.Schedule.Create(
			_reconnectDelay,
			new CallbackEnvelope(_publisher.Publish),
			new GrpcRequestForwardingMessage.Reconnect(leaderId, connectionGeneration)));
	}

	private Task Disconnect()
	{
		_connectionGeneration++;
		var active = _active;
		_active = null;
		active?.Service.Stop();
		return active?.Service.Task ?? Task.CompletedTask;
	}

	private bool HasHealthyStreamTo(MemberInfo leader) =>
		_active is not null &&
		!_active.Service.Task.IsCompleted &&
		_active.LeaderId == leader.InstanceId &&
		Equals(_active.LeaderEndPoint, leader.HttpEndPoint);

	private void PublishIfActive(ActiveStream active, Message message)
	{
		lock (_sync)
		{
			if (ReferenceEquals(_active, active))
			{
				_publisher.Publish(message);
			}
		}
	}

	private bool TryPublishIfActive(ActiveStream active, Message message)
	{
		lock (_sync)
		{
			if (!ReferenceEquals(_active, active))
			{
				return false;
			}

			_publisher.Publish(message);
			return true;
		}
	}

	private static bool IsForwardableWrite(ClientMessage.WriteRequestMessage message) => message is
		ClientMessage.WriteEvents or
		ClientMessage.TransactionStart or
		ClientMessage.TransactionWrite or
		ClientMessage.TransactionCommit or
		ClientMessage.DeleteStream;

	private static bool IsForwardingState(VNodeState state) => state is
		VNodeState.PreReplica or
		VNodeState.PreReadOnlyReplica or
		VNodeState.CatchingUp or
		VNodeState.Clone or
		VNodeState.Follower or
		VNodeState.ReadOnlyReplica;

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

	private sealed class ActiveStream(Guid leaderId, System.Net.EndPoint leaderEndPoint, long connectionGeneration)
	{
		public Guid LeaderId { get; } = leaderId;
		public System.Net.EndPoint LeaderEndPoint { get; } = leaderEndPoint;
		public long ConnectionGeneration { get; } = connectionGeneration;
		public IGrpcRequestForwardingService Service { get; set; }
	}

}
