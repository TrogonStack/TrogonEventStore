using System;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Services.Transport.Tcp;
using EventStore.Plugins.Authentication;
using EventStore.Transport.Tcp;
using EndPoint = System.Net.EndPoint;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.Services.Replication;

public enum TcpForwardingTransport
{
	Plaintext,
	Tls
}

public sealed record TcpForwardingConnectionTarget
{
	private TcpForwardingConnectionTarget(EndPoint endPoint, TcpForwardingTransport transport)
	{
		EndPoint = endPoint;
		Transport = transport;
	}

	public EndPoint EndPoint { get; }
	public TcpForwardingTransport Transport { get; }

	public static TcpForwardingConnectionTarget FromLeader(
		MemberInfo leader,
		TcpForwardingTransport transport)
	{
		if (TryFromLeader(leader, transport, out var target))
		{
			return target;
		}

		throw new InvalidOperationException($"Leader {leader.InstanceId:B} has no {transport} TCP endpoint.");
	}

	public static bool TryFromLeader(
		MemberInfo leader,
		TcpForwardingTransport transport,
		out TcpForwardingConnectionTarget target)
	{
		Ensure.NotNull(leader, nameof(leader));
		if (!Enum.IsDefined(transport))
		{
			throw new ArgumentOutOfRangeException(nameof(transport));
		}

		var endPoint = transport switch
		{
			TcpForwardingTransport.Plaintext => leader.InternalTcpEndPoint,
			TcpForwardingTransport.Tls => leader.InternalSecureTcpEndPoint,
			_ => throw new ArgumentOutOfRangeException(nameof(transport))
		};

		target = endPoint is null ? null : new TcpForwardingConnectionTarget(endPoint, transport);
		return target is not null;
	}
}

public sealed record TcpForwardingConnectionSettings(
	TimeSpan HeartbeatInterval,
	TimeSpan HeartbeatTimeout,
	TimeSpan WriteTimeout);

public interface ITcpForwardingConnection
{
	EndPoint RemoteEndPoint { get; }
	void StartReceiving();
	void Send(Message message);
	void Stop(string reason);
}

public interface ITcpForwardingConnectionFactory
{
	ITcpForwardingConnection Create(
		TcpForwardingConnectionTarget target,
		Action onEstablished,
		Action<SocketError> onClosed);
}

public sealed class TcpForwardingConnectionFactory : ITcpForwardingConnectionFactory
{
	private readonly TcpClientConnector _connector = new();
	private readonly IPublisher _publisher;
	private readonly IPublisher _networkSendQueue;
	private readonly IAuthenticationProvider _authProvider;
	private readonly AuthorizationGateway _authorizationGateway;
	private readonly CertificateDelegates.ServerCertificateValidator _serverCertificateValidator;
	private readonly Func<X509Certificate> _clientCertificateSelector;
	private readonly TcpForwardingConnectionSettings _settings;

	public TcpForwardingConnectionFactory(
		IPublisher publisher,
		IPublisher networkSendQueue,
		IAuthenticationProvider authProvider,
		AuthorizationGateway authorizationGateway,
		CertificateDelegates.ServerCertificateValidator serverCertificateValidator,
		Func<X509Certificate> clientCertificateSelector,
		TcpForwardingConnectionSettings settings)
	{
		_publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
		_networkSendQueue = networkSendQueue ?? throw new ArgumentNullException(nameof(networkSendQueue));
		_authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
		_authorizationGateway = authorizationGateway ?? throw new ArgumentNullException(nameof(authorizationGateway));
		_serverCertificateValidator = serverCertificateValidator;
		_clientCertificateSelector = clientCertificateSelector;
		_settings = settings ?? throw new ArgumentNullException(nameof(settings));
	}

	public ITcpForwardingConnection Create(
		TcpForwardingConnectionTarget target,
		Action onEstablished,
		Action<SocketError> onClosed)
	{
		Ensure.NotNull(target, nameof(target));
		Ensure.NotNull(onEstablished, nameof(onEstablished));
		Ensure.NotNull(onClosed, nameof(onClosed));

		var useTls = target.Transport == TcpForwardingTransport.Tls;
		var connection = new TcpConnectionManager(
			useTls ? "leader-forwarding-secure" : "leader-forwarding-normal",
			Guid.NewGuid(),
			new TcpForwardingDispatcher(_settings.WriteTimeout),
			_publisher,
			target.EndPoint.GetHost(),
			target.EndPoint.GetOtherNames(),
			target.EndPoint,
			_connector,
			useTls,
			_serverCertificateValidator,
			() => new X509CertificateCollection { _clientCertificateSelector() },
			_networkSendQueue,
			_authProvider,
			_authorizationGateway,
			_settings.HeartbeatInterval,
			_settings.HeartbeatTimeout,
			_ => onEstablished(),
			(_, socketError) => onClosed(socketError));

		return new TcpForwardingConnection(connection, _networkSendQueue);
	}

	private sealed class TcpForwardingConnection(
		TcpConnectionManager connection,
		IPublisher networkSendQueue) : ITcpForwardingConnection
	{
		public EndPoint RemoteEndPoint => connection.RemoteEndPoint;

		public void StartReceiving() => connection.StartReceiving();

		public void Send(Message message) => networkSendQueue.Publish(new TcpMessage.TcpSend(connection, message));

		public void Stop(string reason) => connection.Stop(reason);
	}
}

public static partial class TcpRequestForwardingMessage
{
	[DerivedMessage(CoreMessage.Replication)]
	public sealed partial class Reconnect(Guid leaderId, long connectionGeneration) : Message
	{
		public Guid LeaderId { get; } = leaderId;
		public long ConnectionGeneration { get; } = connectionGeneration;
	}
}

public sealed class TcpRequestForwardingService :
	IHandle<SystemMessage.StateChangeMessage>,
	IHandle<ReplicationMessage.ReconnectToLeader>,
	IHandle<ClientMessage.TcpForwardMessage>,
	IHandle<TcpRequestForwardingMessage.Reconnect>,
	IAsyncDisposable
{
	private static readonly ILogger Log = Serilog.Log.ForContext<TcpRequestForwardingService>();
	public static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromMilliseconds(500);

	private readonly object _sync = new();
	private readonly IPublisher _publisher;
	private readonly ITcpForwardingConnectionFactory _connectionFactory;
	private readonly TcpForwardingTransport _transport;
	private readonly TimeSpan _reconnectDelay;

	private VNodeState _state = VNodeState.Initializing;
	private MemberInfo _leader;
	private ITcpForwardingConnection _connection;
	private TcpForwardingConnectionTarget _connectionTarget;
	private long _connectionGeneration;
	private long _scheduledReconnectGeneration = -1;

	public TcpRequestForwardingService(
		IPublisher publisher,
		ITcpForwardingConnectionFactory connectionFactory,
		TcpForwardingTransport transport,
		TimeSpan reconnectDelay)
	{
		_publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
		_connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
		if (!Enum.IsDefined(transport))
		{
			throw new ArgumentOutOfRangeException(nameof(transport));
		}

		if (reconnectDelay < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(reconnectDelay));
		}

		_transport = transport;
		_reconnectDelay = reconnectDelay;
	}

	public void Handle(SystemMessage.StateChangeMessage message)
	{
		lock (_sync)
		{
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
					Disconnect($"Node state changed to {_state}. Closing write forwarding connection.");
					break;
			}
		}
	}

	public void Handle(ReplicationMessage.ReconnectToLeader message)
	{
		lock (_sync)
		{
			if (!IsForwardingState(_state))
			{
				return;
			}

			if (HasHealthyConnectionTo(message.Leader))
			{
				_leader = message.Leader;
				return;
			}

			ConnectToLeader(message.Leader);
		}
	}

	public void Handle(ClientMessage.TcpForwardMessage message)
	{
		if (!IsForwardableWrite(message.Message))
		{
			throw new ArgumentException(
				$"{message.Message.GetType().Name} is not supported by TCP request forwarding.",
				nameof(message));
		}

		lock (_sync)
		{
			if (!IsForwardingState(_state))
			{
				return;
			}

			_connection?.Send(message.Message);
		}
	}

	public void Handle(TcpRequestForwardingMessage.Reconnect message)
	{
		lock (_sync)
		{
			if (!IsForwardingState(_state) ||
				_leader is null ||
				message.LeaderId != _leader.InstanceId ||
				message.ConnectionGeneration != _connectionGeneration ||
				_connection is not null)
			{
				return;
			}

			ConnectToLeader(_leader);
		}
	}

	public ValueTask DisposeAsync()
	{
		lock (_sync)
		{
			_state = VNodeState.Shutdown;
			_leader = null;
			Disconnect("Disposing write forwarding service.");
		}

		return ValueTask.CompletedTask;
	}

	private void ConnectToLeader(MemberInfo leader)
	{
		_leader = leader;
		var connectionGeneration = ++_connectionGeneration;
		_scheduledReconnectGeneration = -1;
		var previous = _connection;
		_connection = null;
		previous?.Stop($"Replacing write forwarding connection with leader {leader.InstanceId:B}.");

		ITcpForwardingConnection connection = null;
		var connectionClosed = 0;
		var suppressReconnect = 0;
		try
		{
			var target = TcpForwardingConnectionTarget.FromLeader(leader, _transport);
			_connectionTarget = target;
			connection = _connectionFactory.Create(
				target,
				() => { },
				socketError =>
				{
					if (Interlocked.Exchange(ref connectionClosed, 1) == 1)
					{
						return;
					}

					if (Volatile.Read(ref suppressReconnect) == 0)
					{
						OnConnectionClosed(connectionGeneration, leader.InstanceId, socketError);
					}
				});

			if (Volatile.Read(ref connectionClosed) == 1 || connectionGeneration != _connectionGeneration)
			{
				connection.Stop("Write forwarding connection closed while it was starting.");
				return;
			}

			_connection = connection;
			connection.StartReceiving();
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to connect the write forwarding service to leader {leader}.", leader);
			Interlocked.Exchange(ref suppressReconnect, 1);
			connection?.Stop("Write forwarding connection failed to start.");
			if (connectionGeneration == _connectionGeneration)
			{
				_connection = null;
				ScheduleReconnect(leader.InstanceId, connectionGeneration);
			}
		}
	}

	private void OnConnectionClosed(long connectionGeneration, Guid leaderId, SocketError socketError)
	{
		lock (_sync)
		{
			if (connectionGeneration != _connectionGeneration)
			{
				return;
			}

			_connection = null;
			Log.Warning(
				"Write forwarding connection to leader {leaderId:B} closed with {socketError}.",
				leaderId,
				socketError);
			if (IsForwardingState(_state) && _leader?.InstanceId == leaderId)
			{
				ScheduleReconnect(leaderId, connectionGeneration);
			}
		}
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
			new TcpRequestForwardingMessage.Reconnect(leaderId, connectionGeneration)));
	}

	private void Disconnect(string reason)
	{
		_connectionGeneration++;
		var connection = _connection;
		_connection = null;
		_connectionTarget = null;
		connection?.Stop(reason);
	}

	private bool HasHealthyConnectionTo(MemberInfo leader) =>
		TcpForwardingConnectionTarget.TryFromLeader(leader, _transport, out var target) &&
		_connection is not null &&
		_leader?.InstanceId == leader.InstanceId &&
		_connectionTarget == target;

	private static bool IsForwardableWrite(Message message) => message is
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
}
