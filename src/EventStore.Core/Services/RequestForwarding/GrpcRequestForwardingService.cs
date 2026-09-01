using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Services.Transport.Grpc.Forwarding;
using EventStore.Core.Settings;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.Services.RequestForwarding;

public enum RequestForwardingAdmission
{
	Accepted,
	QueueFull,
	Closed,
	CredentialsRequireTls
}

public delegate bool TryPublishForwardingResponse(Message message);

public interface IGrpcRequestForwardingService
{
	Task Task { get; }
	Task Start();
	RequestForwardingAdmission TryForward(ClientMessage.WriteRequestMessage message);
	void Stop();
}

public interface IGrpcRequestForwardingServiceFactory
{
	IGrpcRequestForwardingService Create(
		TryPublishForwardingResponse tryPublishResponse,
		Action<ClientMessage.NotHandled> publishLocalFailure,
		EndPoint leaderEndPoint,
		ForwardingSessionGeneration sessionGeneration);
}

public sealed class GrpcRequestForwardingServiceFactory : IGrpcRequestForwardingServiceFactory
{
	private readonly IRequestForwardingGrpcClientFactory _clientFactory;
	private readonly Guid _followerInstanceId;
	private readonly ForwardingTransportSecurity _transportSecurity;
	private readonly int _requestQueueCapacity;

	public GrpcRequestForwardingServiceFactory(
		IRequestForwardingGrpcClientFactory clientFactory,
		Guid followerInstanceId,
		int requestQueueCapacity = ESConsts.MaxConnectionQueueSize)
	{
		_clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
		Ensure.NotEmptyGuid(followerInstanceId, nameof(followerInstanceId));
		Ensure.Positive(requestQueueCapacity, nameof(requestQueueCapacity));

		_followerInstanceId = followerInstanceId;
		_transportSecurity = clientFactory.TransportSecurity;
		_requestQueueCapacity = requestQueueCapacity;
	}

	public IGrpcRequestForwardingService Create(
		TryPublishForwardingResponse tryPublishResponse,
		Action<ClientMessage.NotHandled> publishLocalFailure,
		EndPoint leaderEndPoint,
		ForwardingSessionGeneration sessionGeneration)
	{
		Ensure.NotNull(tryPublishResponse, nameof(tryPublishResponse));
		Ensure.NotNull(publishLocalFailure, nameof(publishLocalFailure));
		Ensure.NotNull(leaderEndPoint, nameof(leaderEndPoint));

		return new GrpcRequestForwardingService(
			tryPublishResponse,
			publishLocalFailure,
			_clientFactory.Create(leaderEndPoint),
			_followerInstanceId,
			leaderEndPoint,
			_requestQueueCapacity,
			sessionGeneration,
			_transportSecurity);
	}
}

public sealed class GrpcRequestForwardingService : IGrpcRequestForwardingService
{
	private static readonly ILogger Log = Serilog.Log.ForContext<GrpcRequestForwardingService>();

	private readonly TryPublishForwardingResponse _tryPublishResponse;
	private readonly Action<ClientMessage.NotHandled> _publishLocalFailure;
	private readonly IRequestForwardingGrpcClient _client;
	private readonly Guid _followerInstanceId;
	private readonly EndPoint _leaderEndPoint;
	private readonly ForwardingSessionGeneration _sessionGeneration;
	private readonly ForwardingTransportSecurity _transportSecurity;
	private readonly Channel<ClientMessage.WriteRequestMessage> _requests;
	private readonly CancellationTokenSource _lifetime = new();
	private readonly object _lifetimeLock = new();
	private readonly object _pendingRequestsLock = new();
	private readonly object _startLock = new();
	private readonly Dictionary<Guid, ClientMessage.WriteRequestMessage> _pendingRequests = new();
	private readonly Guid _sessionId = Guid.NewGuid();

	private int _started;
	private int _acceptingRequests;
	private int _expectedCancellation;
	private bool _lifetimeDisposed;

	public GrpcRequestForwardingService(
		TryPublishForwardingResponse tryPublishResponse,
		Action<ClientMessage.NotHandled> publishLocalFailure,
		IRequestForwardingGrpcClient client,
		Guid followerInstanceId,
		EndPoint leaderEndPoint,
		int requestQueueCapacity,
		ForwardingSessionGeneration sessionGeneration,
		ForwardingTransportSecurity transportSecurity = ForwardingTransportSecurity.Cleartext)
	{
		Ensure.NotNull(tryPublishResponse, nameof(tryPublishResponse));
		Ensure.NotNull(publishLocalFailure, nameof(publishLocalFailure));
		Ensure.NotNull(client, nameof(client));
		Ensure.NotEmptyGuid(followerInstanceId, nameof(followerInstanceId));
		Ensure.NotNull(leaderEndPoint, nameof(leaderEndPoint));
		Ensure.Positive(requestQueueCapacity, nameof(requestQueueCapacity));
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionGeneration.Value, nameof(sessionGeneration));
		if (!Enum.IsDefined(transportSecurity))
		{
			throw new ArgumentOutOfRangeException(nameof(transportSecurity));
		}

		_tryPublishResponse = tryPublishResponse;
		_publishLocalFailure = publishLocalFailure;
		_client = client;
		_followerInstanceId = followerInstanceId;
		_leaderEndPoint = leaderEndPoint;
		_sessionGeneration = sessionGeneration;
		_transportSecurity = transportSecurity;
		_requests = Channel.CreateBounded<ClientMessage.WriteRequestMessage>(
			new BoundedChannelOptions(requestQueueCapacity)
			{
				SingleReader = true,
				SingleWriter = false,
				FullMode = BoundedChannelFullMode.Wait
			});
	}

	public Task Task { get; private set; } = System.Threading.Tasks.Task.CompletedTask;

	public Task Start()
	{
		lock (_startLock)
		{
			if (Interlocked.Exchange(ref _started, 1) != 0)
			{
				throw new InvalidOperationException("The request forwarding stream has already been started.");
			}

			Volatile.Write(ref _acceptingRequests, 1);
			Task = RunAsync();
			return Task;
		}
	}

	public RequestForwardingAdmission TryForward(ClientMessage.WriteRequestMessage message)
	{
		Ensure.NotNull(message, nameof(message));
		if (!IsForwardableWrite(message))
		{
			throw new ArgumentException(
				$"{message.GetType().Name} is not supported by gRPC request forwarding.",
				nameof(message));
		}

		if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _acceptingRequests) == 0)
		{
			return RequestForwardingAdmission.Closed;
		}

		lock (_pendingRequestsLock)
		{
			if (Volatile.Read(ref _acceptingRequests) == 0)
			{
				return RequestForwardingAdmission.Closed;
			}

			if (_transportSecurity == ForwardingTransportSecurity.Cleartext &&
				ForwardingGrpcCodec.RequiresTls(message))
			{
				return RequestForwardingAdmission.CredentialsRequireTls;
			}

			if (!_pendingRequests.TryAdd(message.InternalCorrId, message))
			{
				throw new InvalidOperationException(
					$"A request with correlation ID {message.InternalCorrId:B} is already pending forwarding.");
			}

			if (_requests.Writer.TryWrite(message))
			{
				return RequestForwardingAdmission.Accepted;
			}

			_pendingRequests.Remove(message.InternalCorrId);
		}

		return Volatile.Read(ref _acceptingRequests) == 0
			? RequestForwardingAdmission.Closed
			: RequestForwardingAdmission.QueueFull;
	}

	public void Stop()
	{
		Interlocked.Exchange(ref _expectedCancellation, 1);
		CloseRequests();
		CancelLifetime();
	}

	private async Task RunAsync()
	{
		IRequestForwardingGrpcCall call = null;
		Task requestTask = null;
		Task responseTask = null;

		try
		{
			call = _client.Forward(_lifetime.Token);
			await call.WriteAsync(ForwardingGrpcCodec.ToGrpc(
				new ForwardingSession(_followerInstanceId, _sessionId, _sessionGeneration)));

			requestTask = PumpRequestsAsync(call, _lifetime.Token);
			responseTask = ReadResponsesAsync(call, _lifetime.Token);

			var first = await System.Threading.Tasks.Task.WhenAny(requestTask, responseTask);
			if (first == responseTask)
			{
				try
				{
					await responseTask;
				}
				finally
				{
					CloseRequests();
					CancelLifetime();
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
					CancelLifetime();
				}

				await responseTask;
			}
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
		{
		}
		catch (Exception) when (Volatile.Read(ref _expectedCancellation) != 0)
		{
		}
		catch (Exception exception)
		{
			Log.Warning(exception, "Request forwarding stream to [{leaderEndPoint}] ended unexpectedly.",
				_leaderEndPoint);
		}
		finally
		{
			CloseRequests();
			CancelLifetime();
			await ObserveAsync(requestTask);
			await ObserveAsync(responseTask);
			RejectPendingRequests();
			call?.Dispose();
			_client.Dispose();
			DisposeLifetime();
		}
	}

	private void CancelLifetime()
	{
		lock (_lifetimeLock)
		{
			if (!_lifetimeDisposed)
			{
				_lifetime.Cancel();
			}
		}
	}

	private void DisposeLifetime()
	{
		lock (_lifetimeLock)
		{
			if (_lifetimeDisposed)
			{
				return;
			}

			_lifetime.Dispose();
			_lifetimeDisposed = true;
		}
	}

	private void CloseRequests()
	{
		Volatile.Write(ref _acceptingRequests, 0);
		_requests.Writer.TryComplete();
	}

	private async Task PumpRequestsAsync(
		IRequestForwardingGrpcCall call,
		CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var request in _requests.Reader.ReadAllAsync(cancellationToken))
			{
				await call.WriteAsync(ForwardingGrpcCodec.ToGrpc(request, _transportSecurity));
			}
		}
		finally
		{
			try
			{
				if (!cancellationToken.IsCancellationRequested)
				{
					await call.CompleteRequestAsync();
				}
			}
			finally
			{
				RejectPendingRequests();
			}
		}
	}

	private bool TryCompletePendingRequest(Guid correlationId)
	{
		lock (_pendingRequestsLock)
		{
			return _pendingRequests.Remove(correlationId);
		}
	}

	private void RejectPendingRequests()
	{
		List<ClientMessage.WriteRequestMessage> pendingRequests;
		lock (_pendingRequestsLock)
		{
			pendingRequests = new List<ClientMessage.WriteRequestMessage>(_pendingRequests.Values);
			_pendingRequests.Clear();
		}

		foreach (var request in pendingRequests)
		{
			_publishLocalFailure(new ClientMessage.NotHandled(
				request.InternalCorrId,
				ClientMessage.NotHandled.Types.NotHandledReason.NotReady,
				"Request forwarding ended before the request completed."));
		}
	}

	private async Task ReadResponsesAsync(
		IRequestForwardingGrpcCall call,
		CancellationToken cancellationToken)
	{
		await foreach (var response in call.ReadAllAsync(cancellationToken))
		{
			var message = ForwardingGrpcCodec.FromGrpc(response);
			var correlationId = Uuid.FromDto(response.Response.RequestId).ToGuid();
			if (!TryCompletePendingRequest(correlationId))
			{
				continue;
			}

			if (!_tryPublishResponse(message))
			{
				_publishLocalFailure(new ClientMessage.NotHandled(
					correlationId,
					ClientMessage.NotHandled.Types.NotHandledReason.NotReady,
					"Request forwarding response arrived after the stream was replaced."));
			}
		}
	}

	private static bool IsForwardableWrite(ClientMessage.WriteRequestMessage message) => message is
		ClientMessage.WriteEvents or
		ClientMessage.TransactionStart or
		ClientMessage.TransactionWrite or
		ClientMessage.TransactionCommit or
		ClientMessage.DeleteStream;

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
}
