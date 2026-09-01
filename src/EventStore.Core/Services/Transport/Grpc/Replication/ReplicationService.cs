#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using EventStore.Core.Authorization;
using EventStore.Core.Bus;
using EventStore.Core.Cluster.Settings;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Replication;
using EventStore.Plugins.Authorization;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Proto = EventStore.Replication;

namespace EventStore.Core.Services.Transport.Grpc.Replication;

public enum ReplicationAvailability
{
	Unavailable,
	Available
}

public sealed class ReplicationService : Proto.Replication.ReplicationBase
{
	private const int DefaultResponseQueueCapacity = LeaderReplicationService.MaxQueueSize;
	private static readonly Operation ReplicationOperation = new(ReplicationOperations.Connect);
	private readonly IPublisher _publisher;
	private readonly IAuthorizationProvider _authorizationProvider;
	private readonly int _responseQueueCapacity;
	private readonly ReplicationAvailability _availability;
	private readonly object _sessionsLock = new();
	private readonly Dictionary<ReplicationSessionIdentity, GrpcReplicationSession> _sessions = new();

	public ReplicationService(
		IPublisher publisher,
		IAuthorizationProvider authorizationProvider,
		int responseQueueCapacity = DefaultResponseQueueCapacity,
		ReplicationAvailability availability = ReplicationAvailability.Available)
	{
		ArgumentNullException.ThrowIfNull(publisher);
		ArgumentNullException.ThrowIfNull(authorizationProvider);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(responseQueueCapacity);
		if (!Enum.IsDefined(availability))
		{
			throw new ArgumentOutOfRangeException(nameof(availability));
		}

		_publisher = publisher;
		_authorizationProvider = authorizationProvider;
		_responseQueueCapacity = responseQueueCapacity;
		_availability = availability;
	}

	public override async Task Replicate(
		IAsyncStreamReader<Proto.ReplicaFrame> requestStream,
		IServerStreamWriter<Proto.LeaderFrame> responseStream,
		ServerCallContext context)
	{
		var httpContext = context.GetHttpContext();
		var user = httpContext.User;
		if (!await _authorizationProvider.CheckAccessAsync(
				user, ReplicationOperation, context.CancellationToken).ConfigureAwait(false))
		{
			throw RpcExceptions.AccessDenied();
		}
		if (_availability == ReplicationAvailability.Unavailable)
		{
			throw new RpcException(new Status(
				StatusCode.FailedPrecondition, "Replication is not available on a single-node server."));
		}

		if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
		{
			throw RpcExceptions.InvalidArgument("The first replication frame must subscribe the replica.");
		}

		var firstFrame = requestStream.Current;
		if (firstFrame.PayloadCase != Proto.ReplicaFrame.PayloadOneofCase.Subscribe)
		{
			throw RpcExceptions.InvalidArgument("The first replication frame must subscribe the replica.");
		}

		ValidateSubscribeFrame(firstFrame.Subscribe);
		Guid replicaInstanceId;
		ReplicationMessage.SubscribeReplica subscribe;
		try
		{
			replicaInstanceId = Uuid.FromDto(firstFrame.Subscribe.ReplicaInstanceId).ToGuid();
			subscribe = (ReplicationMessage.SubscribeReplica)ReplicationGrpcCodec.FromGrpc(firstFrame);
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
			OverflowException or NullReferenceException)
		{
			throw RpcExceptions.InvalidArgument("The subscribe frame contains an invalid value.");
		}

		if (replicaInstanceId == Guid.Empty)
		{
			throw RpcExceptions.InvalidArgument("The replica instance ID must not be empty.");
		}
		var sessionIdentity = GetSessionIdentity(httpContext, replicaInstanceId);

		await using var session = new GrpcReplicationSession(
			sessionIdentity, Guid.NewGuid(), responseStream, _responseQueueCapacity, context.CancellationToken);
		session.RecordReceived(firstFrame);
		var correlationId = Guid.NewGuid();
		var request = new ReplicationMessage.ReplicaSubscriptionRequest(
			correlationId,
			new CallbackEnvelope(message => session.TrySend(message)),
			session,
			subscribe.Version,
			subscribe.LogPosition,
			subscribe.ChunkId,
			subscribe.LastEpochs.Select(epoch =>
				new Epoch(epoch.EpochPosition, epoch.EpochNumber, epoch.EpochId)).ToArray(),
			subscribe.ReplicaEndPoint,
			subscribe.LeaderId,
			subscribe.SubscriptionId,
			subscribe.IsPromotable);

		lock (_sessionsLock)
		{
			if (_sessions.TryGetValue(sessionIdentity, out var previous))
			{
				previous.Close("A newer replication session replaced this session.");
			}
			_sessions[sessionIdentity] = session;
			_publisher.Publish(request);
		}

		Exception? requestFailure = null;
		var lastAcknowledgedReplicationPosition = long.MinValue;
		var lastAcknowledgedWriterPosition = long.MinValue;
		try
		{
			while (await requestStream.MoveNext(session.CancellationToken).ConfigureAwait(false))
			{
				var frame = requestStream.Current;
				session.RecordReceived(frame);
				if (frame.PayloadCase != Proto.ReplicaFrame.PayloadOneofCase.Acknowledgement)
				{
					throw RpcExceptions.InvalidArgument(
						"Only acknowledgement frames are allowed after the initial subscription.");
				}

				ReplicationMessage.ReplicaLogPositionAck acknowledgement;
				try
				{
					acknowledgement = (ReplicationMessage.ReplicaLogPositionAck)
						ReplicationGrpcCodec.FromGrpc(frame);
				}
				catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
					OverflowException or NullReferenceException)
				{
					throw RpcExceptions.InvalidArgument("The acknowledgement frame contains an invalid value.");
				}
				if (acknowledgement.SubscriptionId != subscribe.SubscriptionId)
				{
					throw RpcExceptions.InvalidArgument(
						"The acknowledgement subscription ID does not match the active subscription.");
				}
				ValidateAcknowledgementPositions(
					acknowledgement,
					lastAcknowledgedReplicationPosition,
					lastAcknowledgedWriterPosition);
				lastAcknowledgedReplicationPosition = acknowledgement.ReplicationLogPosition;
				lastAcknowledgedWriterPosition = acknowledgement.WriterLogPosition;
				_publisher.Publish(acknowledgement);
			}
		}
		catch (OperationCanceledException) when (session.TerminalFailure is not null)
		{
		}
		catch (OperationCanceledException) when (
			!context.CancellationToken.IsCancellationRequested && session.IsClosed)
		{
		}
		catch (Exception exception)
		{
			requestFailure = exception;
		}
		finally
		{
			session.Close("The replication request stream ended.");
			lock (_sessionsLock)
			{
				if (_sessions.TryGetValue(sessionIdentity, out var current) &&
					ReferenceEquals(current, session))
				{
					_sessions.Remove(sessionIdentity);
				}
			}
		}

		RpcException? responseFailure = null;
		try
		{
			await session.Completion.ConfigureAwait(false);
		}
		catch (RpcException exception)
		{
			responseFailure = exception;
		}

		if (requestFailure is not null)
		{
			ExceptionDispatchInfo.Capture(requestFailure).Throw();
		}

		if (responseFailure is not null)
		{
			throw responseFailure;
		}
	}

	private static ReplicationSessionIdentity GetSessionIdentity(
		HttpContext context,
		Guid replicaInstanceId)
	{
		if (context.Connection.ClientCertificate is { } clientCertificate)
		{
			return ReplicationSessionIdentity.ForClientCertificate(replicaInstanceId, clientCertificate);
		}
		if (!context.Request.IsHttps)
		{
			return ReplicationSessionIdentity.ForInsecureSystem(replicaInstanceId);
		}

		throw new RpcException(new Status(
			StatusCode.Unauthenticated,
			"A client certificate is required for secure replication."));
	}

	private static void ValidateSubscribeFrame(Proto.SubscribeReplica subscribe)
	{
		if (subscribe.Version != ReplicationSubscriptionVersions.V_CURRENT)
		{
			throw RpcExceptions.InvalidArgument(
				$"Replication protocol version {subscribe.Version} is not supported.");
		}
		if (subscribe.LogPosition < 0)
		{
			throw RpcExceptions.InvalidArgument("The replication log position must not be negative.");
		}
		if (subscribe.LastEpochs.Count > ClusterConsts.SubscriptionLastEpochCount)
		{
			throw RpcExceptions.InvalidArgument("The subscribe frame contains too many epochs.");
		}
		if (!HasValue(subscribe.ChunkId) || !HasValue(subscribe.LeaderId) ||
			!HasValue(subscribe.SubscriptionId) || !HasValue(subscribe.ReplicaInstanceId) ||
			subscribe.LastEpochs.Any(epoch => !HasValue(epoch.EpochId)))
		{
			throw RpcExceptions.InvalidArgument("The subscribe frame contains an invalid UUID.");
		}
		if (subscribe.LastEpochs.Any(epoch => epoch.EpochPosition < 0 || epoch.EpochNumber < 0))
		{
			throw RpcExceptions.InvalidArgument("The subscribe frame contains an invalid epoch.");
		}
		if (subscribe.AdvertisedEndpoint is null ||
			string.IsNullOrWhiteSpace(subscribe.AdvertisedEndpoint.Address) ||
			subscribe.AdvertisedEndpoint.Port is 0 or > 65_535)
		{
			throw RpcExceptions.InvalidArgument("The advertised replication endpoint is invalid.");
		}
	}

	private static void ValidateAcknowledgementPositions(
		ReplicationMessage.ReplicaLogPositionAck acknowledgement,
		long lastAcknowledgedReplicationPosition,
		long lastAcknowledgedWriterPosition)
	{
		if (acknowledgement.ReplicationLogPosition < 0 || acknowledgement.WriterLogPosition < 0)
		{
			throw RpcExceptions.InvalidArgument("Acknowledgement positions must not be negative.");
		}
		if (acknowledgement.WriterLogPosition > acknowledgement.ReplicationLogPosition)
		{
			throw RpcExceptions.InvalidArgument(
				"The acknowledgement writer position must not exceed its replication position.");
		}
		if (acknowledgement.ReplicationLogPosition < lastAcknowledgedReplicationPosition ||
			acknowledgement.WriterLogPosition < lastAcknowledgedWriterPosition)
		{
			throw RpcExceptions.InvalidArgument("Acknowledgement positions must not regress.");
		}
	}

	private static bool HasValue(EventStore.Client.UUID? value) =>
		value is { ValueCase: not EventStore.Client.UUID.ValueOneofCase.None };
}
