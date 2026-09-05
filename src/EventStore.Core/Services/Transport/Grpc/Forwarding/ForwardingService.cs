#nullable enable

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Core.Authentication;
using EventStore.Core.Authentication.InternalAuthentication;
using EventStore.Core.Authorization;
using EventStore.Core.Bus;
using EventStore.Core.DataStructures;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.UserManagement;
using EventStore.Plugins.Authentication;
using EventStore.Plugins.Authorization;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Proto = EventStore.Forwarding;

namespace EventStore.Core.Services.Transport.Grpc.Forwarding;

public sealed class ForwardingService : Proto.RequestForwarding.RequestForwardingBase
{
	private const int DefaultSessionCapacity = 100;
	private const int DefaultSessionRegistryCapacity = 100;
	private static readonly Operation ForwardingOperation = new(ForwardingOperations.Connect);
	private readonly IPublisher _publisher;
	private readonly IAuthorizationProvider _authorizationProvider;
	private readonly IAuthenticationProvider _authenticationProvider;
	private readonly int _sessionCapacity;
	private readonly object _sessionsLock = new();
	private readonly IStickyLRUCache<ForwardingSessionIdentity, ActiveForwardingSession> _sessions;

	public ForwardingService(
		IPublisher publisher,
		IAuthorizationProvider authorizationProvider,
		IAuthenticationProvider authenticationProvider,
		int sessionCapacity = DefaultSessionCapacity,
		int sessionRegistryCapacity = DefaultSessionRegistryCapacity)
	{
		ArgumentNullException.ThrowIfNull(publisher);
		ArgumentNullException.ThrowIfNull(authorizationProvider);
		ArgumentNullException.ThrowIfNull(authenticationProvider);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionCapacity);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionRegistryCapacity);

		_publisher = publisher;
		_authorizationProvider = authorizationProvider;
		_authenticationProvider = authenticationProvider;
		_sessionCapacity = sessionCapacity;
		_sessions = new StickyLRUCache<ForwardingSessionIdentity, ActiveForwardingSession>(
			sessionRegistryCapacity);
	}

	public override async Task Forward(
		IAsyncStreamReader<Proto.FollowerFrame> requestStream,
		IServerStreamWriter<Proto.LeaderFrame> responseStream,
		ServerCallContext context)
	{
		var httpContext = context.GetHttpContext();
		if (!await _authorizationProvider.CheckAccessAsync(
				httpContext.User, ForwardingOperation, context.CancellationToken).ConfigureAwait(false))
		{
			throw RpcExceptions.AccessDenied();
		}

		RequireNodeAuthentication(httpContext);

		if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false) ||
			requestStream.Current.PayloadCase != Proto.FollowerFrame.PayloadOneofCase.Open)
		{
			throw RpcExceptions.InvalidArgument("The first forwarding frame must open the session.");
		}

		var forwardingSession = ValidateOpenSession(requestStream.Current.Open);
		var sessionIdentity = GetSessionIdentity(httpContext, forwardingSession.FollowerInstanceId);

		await using var responses = new ForwardingResponseSession(
			responseStream,
			_sessionCapacity,
			context.CancellationToken);
		var activeSession = new ActiveForwardingSession(
			forwardingSession.SessionId,
			forwardingSession.Generation,
			responses);
		lock (_sessionsLock)
		{
			var hasCurrent = _sessions.TryGet(sessionIdentity, out var current);
			if (hasCurrent &&
				!activeSession.CanReplace(current))
			{
				responses.Fail(new RpcException(new Status(
					StatusCode.Cancelled,
					"A newer forwarding session is already active.")));
			}
			else
			{
				current?.Responses?.Fail(new RpcException(new Status(
					StatusCode.Cancelled,
					"A newer forwarding session replaced this session.")));
				_sessions.Put(
					sessionIdentity,
					activeSession,
					hasCurrent && current?.Responses is not null ? 0 : 1);
			}
		}

		try
		{
			while (await requestStream.MoveNext(responses.CancellationToken).ConfigureAwait(false))
			{
				var frame = requestStream.Current;
				if (frame.PayloadCase != Proto.FollowerFrame.PayloadOneofCase.Request)
				{
					throw RpcExceptions.InvalidArgument(
						"Only request frames are allowed after the forwarding session is opened.");
				}

				await HandleRequest(frame.Request, responses, context).ConfigureAwait(false);
			}

			responses.CompleteRequests();
			await responses.Completion.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (responses.TerminalFailure is { } terminalFailure)
		{
			throw terminalFailure;
		}
		catch (Exception exception)
		{
			responses.Fail(exception);
			throw;
		}
		finally
		{
			lock (_sessionsLock)
			{
				if (_sessions.TryGet(sessionIdentity, out var current) &&
					ReferenceEquals(current, activeSession))
				{
					_sessions.Put(sessionIdentity, activeSession.WithoutResponses(), -1);
				}
			}
		}
	}

	private async Task HandleRequest(
		Proto.ForwardRequest request,
		ForwardingResponseSession responses,
		ServerCallContext context)
	{
		Guid requestId;
		ForwardingIdentity identity;
		try
		{
			requestId = Uuid.FromDto(request.RequestId).ToGuid();
			identity = ForwardingGrpcCodec.GetIdentity(request.Identity);
		}
		catch (Exception exception) when (IsInvalidProtocolValue(exception))
		{
			throw RpcExceptions.InvalidArgument("The forwarding request contains an invalid value.");
		}

		if (requestId == Guid.Empty)
		{
			throw RpcExceptions.InvalidArgument("The forwarding request ID must not be empty.");
		}

		var authentication = await AuthenticateAsync(identity, context).ConfigureAwait(false);
		if (authentication.Failure is { } failure)
		{
			await responses.SendAsync(failure(requestId)).ConfigureAwait(false);
			return;
		}

		if (!responses.TryReserveResponse(out var response))
		{
			await responses.SendAsync(new ClientMessage.NotHandled(
				requestId,
				ClientMessage.NotHandled.Types.NotHandledReason.TooBusy,
				"The forwarding service has too many outstanding requests.")).ConfigureAwait(false);
			return;
		}

		ClientMessage.WriteRequestMessage message;
		try
		{
			message = ForwardingGrpcCodec.FromGrpc(
				request,
				response,
				authentication.Principal!,
				authentication.Tokens,
				context.CancellationToken);
		}
		catch (Exception exception) when (IsInvalidProtocolValue(exception))
		{
			response.Cancel();
			throw RpcExceptions.InvalidArgument("The forwarding request contains an invalid value.");
		}

		try
		{
			_publisher.Publish(message);
		}
		catch
		{
			response.Cancel();
			throw;
		}
	}

	private async Task<ForwardedAuthentication> AuthenticateAsync(
		ForwardingIdentity identity,
		ServerCallContext context)
	{
		switch (identity)
		{
			case ForwardingIdentity.TrustedSystem:
				return ForwardedAuthentication.Authenticated(SystemAccounts.System, null);
			case ForwardingIdentity.Anonymous:
				return ForwardedAuthentication.Authenticated(SystemAccounts.Anonymous, null);
			case ForwardingIdentity.LocalSession session:
				return await AuthenticateSessionAsync(session, context).ConfigureAwait(false);
			case ForwardingIdentity.BearerToken bearer:
				return await AuthenticateCredentialsAsync(
					context,
					new Dictionary<string, string> { [AuthenticationTokenKeys.Jwt] = bearer.Token })
					.ConfigureAwait(false);
			case ForwardingIdentity.UserPassword userPassword:
				return await AuthenticateCredentialsAsync(
					context,
					new Dictionary<string, string>
					{
						[AuthenticationTokenKeys.Username] = userPassword.Username,
						[AuthenticationTokenKeys.Password] = userPassword.Password
					}).ConfigureAwait(false);
			default:
				throw RpcExceptions.InvalidArgument("The forwarded identity is invalid.");
		}
	}

	private async Task<ForwardedAuthentication> AuthenticateSessionAsync(
		ForwardingIdentity.LocalSession session,
		ServerCallContext context)
	{
		var httpContext = context.GetHttpContext();
		if (!httpContext.Request.IsHttps || httpContext.Connection.ClientCertificate is null ||
			httpContext.User != SystemAccounts.System ||
			string.IsNullOrWhiteSpace(session.Username) || session.UserEventId == Guid.Empty ||
			_authenticationProvider is not ISessionAuthenticationProvider provider)
		{
			return ForwardedAuthentication.NotAuthenticated("Invalid session authentication.");
		}

		var principal = new ClaimsPrincipal(new ClaimsIdentity([
			new Claim(ClaimTypes.Name, session.Username),
			new Claim(InternalAuthenticationProvider.SessionSecurityStampClaimType, session.UserEventId.ToString("N"))
		], "ES-SessionForwarding"));
		try
		{
			var validated = await provider.ValidateSessionAsync(principal, context.CancellationToken).ConfigureAwait(false);
			return validated is null
				? ForwardedAuthentication.NotAuthenticated("Invalid session authentication.")
				: ForwardedAuthentication.Authenticated(validated, null);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			return ForwardedAuthentication.NotAuthenticated("Session authentication failed.");
		}
	}

	private async Task<ForwardedAuthentication> AuthenticateCredentialsAsync(
		ServerCallContext context,
		IReadOnlyDictionary<string, string> tokens)
	{
		var authentication = new ForwardedAuthenticationRequest(context.Peer, tokens);
		try
		{
			_authenticationProvider.Authenticate(authentication);
		}
		catch
		{
			return ForwardedAuthentication.NotAuthenticated("Internal Server Error");
		}

		return await authentication.Completion
			.WaitAsync(context.CancellationToken)
			.ConfigureAwait(false);
	}

	private static void RequireNodeAuthentication(HttpContext context)
	{
		if (context.Request.IsHttps && context.Connection.ClientCertificate is null)
		{
			throw new RpcException(new Status(
				StatusCode.Unauthenticated,
				"A client certificate is required for secure request forwarding."));
		}
	}

	private static ForwardingSession ValidateOpenSession(Proto.OpenSession open)
	{
		try
		{
			var session = ForwardingGrpcCodec.FromGrpc(open);
			if (session.FollowerInstanceId == Guid.Empty || session.SessionId == Guid.Empty)
			{
				throw RpcExceptions.InvalidArgument("The forwarding session IDs must not be empty.");
			}

			return session;
		}
		catch (RpcException)
		{
			throw;
		}
		catch (Exception exception) when (IsInvalidProtocolValue(exception))
		{
			throw RpcExceptions.InvalidArgument("The forwarding session contains an invalid value.");
		}
	}

	private static ForwardingSessionIdentity GetSessionIdentity(
		HttpContext context,
		Guid followerInstanceId) => context.Connection.ClientCertificate is { } clientCertificate
		? ForwardingSessionIdentity.ForClientCertificate(followerInstanceId, clientCertificate)
		: ForwardingSessionIdentity.ForInsecureSystem(followerInstanceId);

	private static bool IsInvalidProtocolValue(Exception exception) =>
		exception is ArgumentException or InvalidOperationException or OverflowException or NullReferenceException;

	private sealed record ActiveForwardingSession(
		Guid SessionId,
		ForwardingSessionGeneration Generation,
		ForwardingResponseSession? Responses)
	{
		public bool CanReplace(ActiveForwardingSession other) =>
			SessionId != other.SessionId && Generation.IsNewerThan(other.Generation);

		public ActiveForwardingSession WithoutResponses() => this with { Responses = null };
	}

	private readonly record struct ForwardedAuthentication(
		ClaimsPrincipal? Principal,
		IReadOnlyDictionary<string, string>? Tokens,
		Func<Guid, Message>? Failure)
	{
		public static ForwardedAuthentication Authenticated(
			ClaimsPrincipal principal,
			IReadOnlyDictionary<string, string>? tokens) => new(principal, tokens, null);

		public static ForwardedAuthentication NotAuthenticated(string reason) => new(
			null,
			null,
			requestId => new TcpMessage.NotAuthenticated(requestId, reason));

		public static ForwardedAuthentication NotReady(string reason) => new(
			null,
			null,
			requestId => new ClientMessage.NotHandled(
				requestId,
				ClientMessage.NotHandled.Types.NotHandledReason.NotReady,
				reason));
	}

	private sealed class ForwardedAuthenticationRequest(
		string id,
		IReadOnlyDictionary<string, string> tokens) : AuthenticationRequest(id, tokens)
	{
		private readonly TaskCompletionSource<ForwardedAuthentication> _completion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<ForwardedAuthentication> Completion => _completion.Task;

		public override void Unauthorized() =>
			_completion.TrySetResult(ForwardedAuthentication.NotAuthenticated("Not Authenticated"));

		public override void Authenticated(ClaimsPrincipal principal) =>
			_completion.TrySetResult(ForwardedAuthentication.Authenticated(principal, Tokens));

		public override void Error() =>
			_completion.TrySetResult(ForwardedAuthentication.NotAuthenticated("Internal Server Error"));

		public override void NotReady() =>
			_completion.TrySetResult(ForwardedAuthentication.NotReady("Server not ready"));
	}

	private sealed class ForwardingResponseSession : IAsyncDisposable
	{
		private readonly object _lock = new();
		private readonly Channel<QueuedResponse> _responses;
		private readonly CancellationTokenSource _lifetime;
		private readonly SemaphoreSlim _responseSlots;
		private int _activeResponses;
		private bool _requestsCompleted;
		private bool _failed;
		private Exception? _terminalFailure;

		public ForwardingResponseSession(
			IServerStreamWriter<Proto.LeaderFrame> responseStream,
			int capacity,
			CancellationToken cancellationToken)
		{
			_responseSlots = new SemaphoreSlim(capacity, capacity);
			_responses = Channel.CreateBounded<QueuedResponse>(new BoundedChannelOptions(capacity)
			{
				SingleReader = true,
				SingleWriter = false,
				FullMode = BoundedChannelFullMode.Wait
			});
			_lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			Completion = PumpAsync(responseStream);
		}

		public CancellationToken CancellationToken => _lifetime.Token;
		public Task Completion { get; }
		public Exception? TerminalFailure => Volatile.Read(ref _terminalFailure);

		public bool TryReserveResponse(out ForwardingResponseReservation response)
		{
			lock (_lock)
			{
				if (_requestsCompleted || _failed || !_responseSlots.Wait(0))
				{
					response = null!;
					return false;
				}

				_activeResponses++;
			}

			response = new ForwardingResponseReservation(this);
			return true;
		}

		public async ValueTask SendAsync(Message message)
		{
			await _responseSlots.WaitAsync(_lifetime.Token).ConfigureAwait(false);
			lock (_lock)
			{
				if (_failed)
				{
					_responseSlots.Release();
					throw _terminalFailure ?? new OperationCanceledException(_lifetime.Token);
				}
				if (_lifetime.IsCancellationRequested)
				{
					_responseSlots.Release();
					throw new OperationCanceledException(_lifetime.Token);
				}

				_activeResponses++;
			}

			new ForwardingResponseReservation(this).ReplyWith(message);
		}

		private void QueueResponse(Message message, ForwardingResponseReservation response)
		{
			QueuedResponse queuedResponse;
			try
			{
				queuedResponse = new QueuedResponse(ForwardingGrpcCodec.ToGrpc(message), response);
			}
			catch (Exception exception)
			{
				response.CompleteDelivery();
				Fail(exception);
				return;
			}

			if (!_responses.Writer.TryWrite(queuedResponse))
			{
				response.CompleteDelivery();
				Fail(new RpcException(new Status(
					StatusCode.Internal,
					"The forwarding response capacity invariant was violated.")));
			}
		}

		public void CompleteRequests()
		{
			lock (_lock)
			{
				_requestsCompleted = true;
				if (_activeResponses == 0 && !_failed)
				{
					_responses.Writer.TryComplete();
				}
			}
		}

		public void Fail(Exception exception)
		{
			lock (_lock)
			{
				if (_failed)
				{
					return;
				}

				_failed = true;
				_terminalFailure = exception;
				_responses.Writer.TryComplete(exception);
				_lifetime.Cancel();
			}
		}

		public async ValueTask DisposeAsync()
		{
			_lifetime.Cancel();
			_responses.Writer.TryComplete();
			try
			{
				await Completion.ConfigureAwait(false);
			}
			catch
			{
			}
			_lifetime.Dispose();
		}

		private void CompleteResponse()
		{
			lock (_lock)
			{
				_activeResponses--;
				_responseSlots.Release();
				if (_requestsCompleted && _activeResponses == 0 && !_failed)
				{
					_responses.Writer.TryComplete();
				}
			}
		}

		private async Task PumpAsync(IServerStreamWriter<Proto.LeaderFrame> responseStream)
		{
			try
			{
				await foreach (var response in _responses.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
				{
					try
					{
						await responseStream.WriteAsync(response.Frame).ConfigureAwait(false);
					}
					catch (Exception exception)
					{
						Fail(exception);
						throw;
					}
					finally
					{
						response.Reservation.CompleteDelivery();
					}
				}
			}
			catch (Exception exception)
			{
				Fail(exception);
				while (_responses.Reader.TryRead(out var response))
				{
					response.Reservation.CompleteDelivery();
				}
				throw;
			}
		}

		public sealed class ForwardingResponseReservation(ForwardingResponseSession owner) : IEnvelope
		{
			private int _completed;
			private int _deliveryCompleted;

			public void ReplyWith<T>(T message) where T : Message
			{
				if (Interlocked.Exchange(ref _completed, 1) != 0)
				{
					return;
				}

				owner.QueueResponse(message, this);
			}

			public void Cancel()
			{
				if (Interlocked.Exchange(ref _completed, 1) == 0)
				{
					CompleteDelivery();
				}
			}

			public void CompleteDelivery()
			{
				if (Interlocked.Exchange(ref _deliveryCompleted, 1) == 0)
				{
					owner.CompleteResponse();
				}
			}
		}

		private readonly record struct QueuedResponse(
			Proto.LeaderFrame Frame,
			ForwardingResponseReservation Reservation);
	}
}
