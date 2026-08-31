using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using EventStore.Common.Utils;
using EventStore.Core.Authentication;
using EventStore.Core.Authentication.DelegatedAuthentication;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.UserManagement;
using Google.Protobuf;
using Proto = EventStore.Forwarding;

namespace EventStore.Core.Services.Transport.Grpc.Forwarding;

public abstract record ForwardingIdentity
{
	private ForwardingIdentity()
	{
	}

	public sealed record TrustedSystem : ForwardingIdentity;
	public sealed record BearerToken(string Token) : ForwardingIdentity;
	public sealed record UserPassword(string Username, string Password) : ForwardingIdentity;
	public sealed record Anonymous : ForwardingIdentity;
}

public readonly record struct ForwardingSessionGeneration
{
	public ForwardingSessionGeneration(long value)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
		Value = value;
	}

	public long Value { get; }

	public bool IsNewerThan(ForwardingSessionGeneration other) => Value > other.Value;
}

public readonly record struct ForwardingSession(
	Guid FollowerInstanceId,
	Guid SessionId,
	ForwardingSessionGeneration Generation);

public enum ForwardingTransportSecurity
{
	Cleartext,
	Tls
}

public static class ForwardingGrpcCodec
{
	public static Proto.FollowerFrame ToGrpc(ForwardingSession session) => new()
	{
		Open = new Proto.OpenSession
		{
			FollowerInstanceId = Uuid.FromGuid(session.FollowerInstanceId).ToDto(),
			SessionId = Uuid.FromGuid(session.SessionId).ToDto(),
			ConnectionGeneration = session.Generation.Value
		}
	};

	public static ForwardingSession FromGrpc(Proto.OpenSession session) => new(
		Uuid.FromDto(session.FollowerInstanceId).ToGuid(),
		Uuid.FromDto(session.SessionId).ToGuid(),
		new ForwardingSessionGeneration(session.ConnectionGeneration));

	public static Proto.FollowerFrame ToGrpc(
		ClientMessage.WriteRequestMessage message,
		ForwardingTransportSecurity transportSecurity) => new()
		{
			Request = ToGrpcRequest(message, transportSecurity)
		};

	public static ForwardingIdentity GetIdentity(Proto.ForwardedIdentity identity) => identity.IdentityCase switch
	{
		Proto.ForwardedIdentity.IdentityOneofCase.TrustedSystem => new ForwardingIdentity.TrustedSystem(),
		Proto.ForwardedIdentity.IdentityOneofCase.BearerToken =>
			new ForwardingIdentity.BearerToken(identity.BearerToken),
		Proto.ForwardedIdentity.IdentityOneofCase.UserPassword =>
			new ForwardingIdentity.UserPassword(identity.UserPassword.Username, identity.UserPassword.Password),
		Proto.ForwardedIdentity.IdentityOneofCase.Anonymous => new ForwardingIdentity.Anonymous(),
		_ => throw new ArgumentOutOfRangeException(nameof(identity), identity.IdentityCase,
			"Unknown forwarded identity")
	};

	public static bool RequiresTls(ClientMessage.WriteRequestMessage message) =>
		GetIdentity(message) is ForwardingIdentity.BearerToken or ForwardingIdentity.UserPassword;

	public static ClientMessage.WriteRequestMessage FromGrpc(
		Proto.ForwardRequest request,
		IEnvelope envelope,
		ClaimsPrincipal user,
		IReadOnlyDictionary<string, string> tokens,
		CancellationToken cancellationToken = default)
	{
		var correlationId = Uuid.FromDto(request.RequestId).ToGuid();
		var internalCorrelationId = Guid.NewGuid();
		return request.PayloadCase switch
		{
			Proto.ForwardRequest.PayloadOneofCase.WriteEvents => new ClientMessage.WriteEvents(
				internalCorrelationId,
				correlationId,
				envelope,
				request.WriteEvents.RequireLeader,
				request.WriteEvents.EventStreamId,
				request.WriteEvents.ExpectedVersion,
				request.WriteEvents.Events.Select(FromGrpc).ToArray(),
				user,
				tokens,
				cancellationToken),
			Proto.ForwardRequest.PayloadOneofCase.TransactionStart => new ClientMessage.TransactionStart(
				internalCorrelationId,
				correlationId,
				envelope,
				request.TransactionStart.RequireLeader,
				request.TransactionStart.EventStreamId,
				request.TransactionStart.ExpectedVersion,
				user,
				tokens,
				cancellationToken),
			Proto.ForwardRequest.PayloadOneofCase.TransactionWrite => new ClientMessage.TransactionWrite(
				internalCorrelationId,
				correlationId,
				envelope,
				request.TransactionWrite.RequireLeader,
				request.TransactionWrite.TransactionId,
				request.TransactionWrite.Events.Select(FromGrpc).ToArray(),
				user,
				tokens,
				cancellationToken),
			Proto.ForwardRequest.PayloadOneofCase.TransactionCommit => new ClientMessage.TransactionCommit(
				internalCorrelationId,
				correlationId,
				envelope,
				request.TransactionCommit.RequireLeader,
				request.TransactionCommit.TransactionId,
				user,
				tokens,
				cancellationToken),
			Proto.ForwardRequest.PayloadOneofCase.DeleteStream => new ClientMessage.DeleteStream(
				internalCorrelationId,
				correlationId,
				envelope,
				request.DeleteStream.RequireLeader,
				request.DeleteStream.EventStreamId,
				request.DeleteStream.ExpectedVersion,
				request.DeleteStream.HardDelete,
				user,
				tokens,
				cancellationToken),
			_ => throw new ArgumentOutOfRangeException(nameof(request), request.PayloadCase,
				"Unknown forwarding request")
		};
	}

	public static Proto.LeaderFrame ToGrpc(Message message) => new()
	{
		Response = message switch
		{
			ClientMessage.WriteEventsCompleted completed => ToGrpc(completed),
			ClientMessage.TransactionStartCompleted completed => ToGrpc(completed),
			ClientMessage.TransactionWriteCompleted completed => ToGrpc(completed),
			ClientMessage.TransactionCommitCompleted completed => ToGrpc(completed),
			ClientMessage.DeleteStreamCompleted completed => ToGrpc(completed),
			ClientMessage.NotHandled notHandled => ToGrpc(notHandled),
			TcpMessage.NotAuthenticated notAuthenticated => ToGrpc(notAuthenticated),
			_ => throw new ArgumentOutOfRangeException(nameof(message), message.GetType().FullName,
				"Unsupported forwarding response")
		}
	};

	public static Message FromGrpc(Proto.LeaderFrame frame)
	{
		ArgumentNullException.ThrowIfNull(frame.Response);
		var response = frame.Response;
		var correlationId = Uuid.FromDto(response.RequestId).ToGuid();
		return response.PayloadCase switch
		{
			Proto.ForwardResponse.PayloadOneofCase.WriteEvents => FromGrpc(correlationId, response.WriteEvents),
			Proto.ForwardResponse.PayloadOneofCase.TransactionStart =>
				FromGrpc(correlationId, response.TransactionStart),
			Proto.ForwardResponse.PayloadOneofCase.TransactionWrite =>
				FromGrpc(correlationId, response.TransactionWrite),
			Proto.ForwardResponse.PayloadOneofCase.TransactionCommit =>
				FromGrpc(correlationId, response.TransactionCommit),
			Proto.ForwardResponse.PayloadOneofCase.DeleteStream => FromGrpc(correlationId, response.DeleteStream),
			Proto.ForwardResponse.PayloadOneofCase.NotHandled => FromGrpc(correlationId, response.NotHandled),
			Proto.ForwardResponse.PayloadOneofCase.NotAuthenticated =>
				new TcpMessage.NotAuthenticated(correlationId, response.NotAuthenticated.Reason),
			_ => throw new ArgumentOutOfRangeException(nameof(frame), response.PayloadCase,
				"Unknown forwarding response")
		};
	}

	private static Proto.ForwardRequest ToGrpcRequest(
		ClientMessage.WriteRequestMessage message,
		ForwardingTransportSecurity transportSecurity)
	{
		var request = new Proto.ForwardRequest
		{
			RequestId = Uuid.FromGuid(message.InternalCorrId).ToDto(),
			Identity = ToGrpcIdentity(message, transportSecurity)
		};

		switch (message)
		{
			case ClientMessage.WriteEvents writeEvents:
				request.WriteEvents = new Proto.WriteEvents
				{
					EventStreamId = writeEvents.EventStreamId,
					ExpectedVersion = writeEvents.ExpectedVersion,
					RequireLeader = writeEvents.RequireLeader
				};
				request.WriteEvents.Events.Add(writeEvents.Events.Select(ToGrpc));
				break;
			case ClientMessage.TransactionStart transactionStart:
				request.TransactionStart = new Proto.TransactionStart
				{
					EventStreamId = transactionStart.EventStreamId,
					ExpectedVersion = transactionStart.ExpectedVersion,
					RequireLeader = transactionStart.RequireLeader
				};
				break;
			case ClientMessage.TransactionWrite transactionWrite:
				request.TransactionWrite = new Proto.TransactionWrite
				{
					TransactionId = transactionWrite.TransactionId,
					RequireLeader = transactionWrite.RequireLeader
				};
				request.TransactionWrite.Events.Add(transactionWrite.Events.Select(ToGrpc));
				break;
			case ClientMessage.TransactionCommit transactionCommit:
				request.TransactionCommit = new Proto.TransactionCommit
				{
					TransactionId = transactionCommit.TransactionId,
					RequireLeader = transactionCommit.RequireLeader
				};
				break;
			case ClientMessage.DeleteStream deleteStream:
				request.DeleteStream = new Proto.DeleteStream
				{
					EventStreamId = deleteStream.EventStreamId,
					ExpectedVersion = deleteStream.ExpectedVersion,
					HardDelete = deleteStream.HardDelete,
					RequireLeader = deleteStream.RequireLeader
				};
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(message), message.GetType().FullName,
					"Unsupported forwarding request");
		}

		return request;
	}

	private static Proto.ForwardedIdentity ToGrpcIdentity(
		ClientMessage.WriteRequestMessage message,
		ForwardingTransportSecurity transportSecurity)
	{
		if (!Enum.IsDefined(transportSecurity))
		{
			throw new ArgumentOutOfRangeException(nameof(transportSecurity));
		}

		var identity = GetIdentity(message);
		if (identity is ForwardingIdentity.BearerToken or ForwardingIdentity.UserPassword)
		{
			EnsureCredentialsAreProtected(transportSecurity);
		}

		return identity switch
		{
			ForwardingIdentity.TrustedSystem => new Proto.ForwardedIdentity
			{
				TrustedSystem = new Google.Protobuf.WellKnownTypes.Empty()
			},
			ForwardingIdentity.BearerToken bearer => new Proto.ForwardedIdentity
			{
				BearerToken = bearer.Token
			},
			ForwardingIdentity.UserPassword userPassword => new Proto.ForwardedIdentity
			{
				UserPassword = new Proto.UserPassword
				{
					Username = userPassword.Username,
					Password = userPassword.Password
				}
			},
			ForwardingIdentity.Anonymous => new Proto.ForwardedIdentity
			{
				Anonymous = new Google.Protobuf.WellKnownTypes.Empty()
			},
			_ => throw new ArgumentOutOfRangeException(nameof(identity), identity.GetType().FullName,
				"Unknown forwarding identity")
		};
	}

	private static ForwardingIdentity GetIdentity(ClientMessage.WriteRequestMessage message)
	{
		if (message.User == SystemAccounts.System)
		{
			return new ForwardingIdentity.TrustedSystem();
		}

		if (message.User is not null)
		{
			foreach (var identity in message.User.Identities.OfType<DelegatedClaimsIdentity>())
			{
				if (identity.FindFirst(AuthenticationTokenKeys.Jwt) is { } jwt)
				{
					return new ForwardingIdentity.BearerToken(jwt.Value);
				}

				if (identity.FindFirst(AuthenticationTokenKeys.Username) is { } uid &&
					identity.FindFirst(AuthenticationTokenKeys.Password) is { } pwd)
				{
					return new ForwardingIdentity.UserPassword(uid.Value, pwd.Value);
				}
			}
		}

		return message.Login is not null && message.Password is not null
			? new ForwardingIdentity.UserPassword(message.Login, message.Password)
			: new ForwardingIdentity.Anonymous();
	}

	private static void EnsureCredentialsAreProtected(ForwardingTransportSecurity transportSecurity)
	{
		if (transportSecurity != ForwardingTransportSecurity.Tls)
		{
			throw new InvalidOperationException(
				"Bearer tokens and passwords cannot be forwarded over a cleartext transport.");
		}
	}

	private static Proto.Event ToGrpc(Event @event) => new()
	{
		EventId = Uuid.FromGuid(@event.EventId).ToDto(),
		EventType = @event.EventType,
		IsJson = @event.IsJson,
		Data = ByteString.CopyFrom(@event.Data),
		Metadata = ByteString.CopyFrom(@event.Metadata),
		IsPropertyMetadata = @event.IsPropertyMetadata
	};

	private static Event FromGrpc(Proto.Event @event) => new(
		Uuid.FromDto(@event.EventId).ToGuid(),
		@event.EventType,
		@event.IsJson,
		@event.Data.ToByteArray(),
		@event.IsPropertyMetadata,
		@event.Metadata.ToByteArray());

	private static Proto.ForwardResponse ToGrpc(ClientMessage.WriteEventsCompleted message)
	{
		var response = NewResponse(message.CorrelationId);
		response.WriteEvents = new Proto.WriteEventsCompleted
		{
			Result = ToGrpc(message.Result),
			Message = message.Message ?? string.Empty,
			FirstEventNumber = message.FirstEventNumber,
			LastEventNumber = message.LastEventNumber,
			PreparePosition = message.PreparePosition,
			CommitPosition = message.CommitPosition,
			CurrentVersion = message.CurrentVersion
		};
		response.WriteEvents.ConsistencyCheckFailures.Add(message.ConsistencyCheckFailures.Select(ToGrpc));
		return response;
	}

	private static Proto.ForwardResponse ToGrpc(ClientMessage.TransactionStartCompleted message)
	{
		var response = NewResponse(message.CorrelationId);
		response.TransactionStart = new Proto.TransactionStartCompleted
		{
			TransactionId = message.TransactionId,
			Result = ToGrpc(message.Result),
			Message = message.Message ?? string.Empty
		};
		return response;
	}

	private static Proto.ForwardResponse ToGrpc(ClientMessage.TransactionWriteCompleted message)
	{
		var response = NewResponse(message.CorrelationId);
		response.TransactionWrite = new Proto.TransactionWriteCompleted
		{
			TransactionId = message.TransactionId,
			Result = ToGrpc(message.Result),
			Message = message.Message ?? string.Empty
		};
		return response;
	}

	private static Proto.ForwardResponse ToGrpc(ClientMessage.TransactionCommitCompleted message)
	{
		var response = NewResponse(message.CorrelationId);
		response.TransactionCommit = new Proto.TransactionCommitCompleted
		{
			TransactionId = message.TransactionId,
			Result = ToGrpc(message.Result),
			Message = message.Message ?? string.Empty,
			FirstEventNumber = message.FirstEventNumber,
			LastEventNumber = message.LastEventNumber,
			PreparePosition = message.PreparePosition,
			CommitPosition = message.CommitPosition
		};
		response.TransactionCommit.ConsistencyCheckFailures.Add(
			message.ConsistencyCheckFailures.Select(ToGrpc));
		return response;
	}

	private static Proto.ForwardResponse ToGrpc(ClientMessage.DeleteStreamCompleted message)
	{
		var response = NewResponse(message.CorrelationId);
		response.DeleteStream = new Proto.DeleteStreamCompleted
		{
			Result = ToGrpc(message.Result),
			Message = message.Message ?? string.Empty,
			CurrentVersion = message.CurrentVersion,
			PreparePosition = message.PreparePosition,
			CommitPosition = message.CommitPosition
		};
		response.DeleteStream.ConsistencyCheckFailures.Add(message.ConsistencyCheckFailures.Select(ToGrpc));
		return response;
	}

	private static Proto.ForwardResponse ToGrpc(ClientMessage.NotHandled message)
	{
		var response = NewResponse(message.CorrelationId);
		var notHandled = new Proto.NotHandled { Reason = ToGrpc(message.Reason) };
		if (message.LeaderInfo is not null)
		{
			notHandled.LeaderInfo = new Proto.LeaderInfo
			{
				ExternalTcp = ToGrpc(message.LeaderInfo.ExternalTcp),
				IsSecure = message.LeaderInfo.IsSecure,
				Http = ToGrpc(message.LeaderInfo.Http)
			};
		}
		else if (message.Description is not null)
		{
			notHandled.Description = message.Description;
		}

		response.NotHandled = notHandled;
		return response;
	}

	private static Proto.ForwardResponse ToGrpc(TcpMessage.NotAuthenticated message)
	{
		var response = NewResponse(message.CorrelationId);
		response.NotAuthenticated = new Proto.NotAuthenticated { Reason = message.Reason ?? string.Empty };
		return response;
	}

	private static Proto.ForwardResponse NewResponse(Guid correlationId) => new()
	{
		RequestId = Uuid.FromGuid(correlationId).ToDto()
	};

	private static ClientMessage.WriteEventsCompleted FromGrpc(
		Guid correlationId,
		Proto.WriteEventsCompleted message) => message.Result == Proto.OperationResult.Success
		? new ClientMessage.WriteEventsCompleted(
			correlationId,
			message.FirstEventNumber,
			message.LastEventNumber,
			message.PreparePosition,
			message.CommitPosition)
		: new ClientMessage.WriteEventsCompleted(
			correlationId,
			FromGrpc(message.Result),
			message.Message,
			message.CurrentVersion,
			message.ConsistencyCheckFailures.Select(FromGrpc).ToArray());

	private static ClientMessage.TransactionStartCompleted FromGrpc(
		Guid correlationId,
		Proto.TransactionStartCompleted message) => new(
		correlationId,
		message.TransactionId,
		FromGrpc(message.Result),
		message.Message);

	private static ClientMessage.TransactionWriteCompleted FromGrpc(
		Guid correlationId,
		Proto.TransactionWriteCompleted message) => new(
		correlationId,
		message.TransactionId,
		FromGrpc(message.Result),
		message.Message);

	private static ClientMessage.TransactionCommitCompleted FromGrpc(
		Guid correlationId,
		Proto.TransactionCommitCompleted message) => message.Result == Proto.OperationResult.Success
		? new ClientMessage.TransactionCommitCompleted(
			correlationId,
			message.TransactionId,
			message.FirstEventNumber,
			message.LastEventNumber,
			message.PreparePosition,
			message.CommitPosition)
		: new ClientMessage.TransactionCommitCompleted(
			correlationId,
			message.TransactionId,
			FromGrpc(message.Result),
			message.Message,
			message.ConsistencyCheckFailures.Select(FromGrpc).ToArray());

	private static ClientMessage.DeleteStreamCompleted FromGrpc(
		Guid correlationId,
		Proto.DeleteStreamCompleted message) => new(
		correlationId,
		FromGrpc(message.Result),
		message.Message,
		message.CurrentVersion,
		message.PreparePosition,
		message.CommitPosition,
		message.ConsistencyCheckFailures.Select(FromGrpc).ToArray());

	private static ClientMessage.NotHandled FromGrpc(Guid correlationId, Proto.NotHandled message)
	{
		var reason = FromGrpc(message.Reason);
		return message.DetailCase switch
		{
			Proto.NotHandled.DetailOneofCase.LeaderInfo => new ClientMessage.NotHandled(
				correlationId,
				reason,
				new ClientMessage.NotHandled.Types.LeaderInfo(
					FromGrpc(message.LeaderInfo.ExternalTcp),
					message.LeaderInfo.IsSecure,
					FromGrpc(message.LeaderInfo.Http))),
			Proto.NotHandled.DetailOneofCase.Description =>
				new ClientMessage.NotHandled(correlationId, reason, message.Description),
			_ => new ClientMessage.NotHandled(correlationId, reason, (string)null)
		};
	}

	private static Proto.ConsistencyCheckFailure ToGrpc(ConsistencyCheckFailure failure)
	{
		var result = new Proto.ConsistencyCheckFailure
		{
			StreamIndex = failure.StreamIndex,
			ExpectedVersion = failure.ExpectedVersion,
			CurrentVersion = failure.CurrentVersion
		};
		if (failure.IsSoftDeleted.HasValue)
		{
			result.IsSoftDeleted = failure.IsSoftDeleted.Value;
		}

		return result;
	}

	private static ConsistencyCheckFailure FromGrpc(Proto.ConsistencyCheckFailure failure) => new(
		failure.StreamIndex,
		failure.ExpectedVersion,
		failure.CurrentVersion,
		failure.HasIsSoftDeleted ? failure.IsSoftDeleted : null);

	private static Proto.EndPoint ToGrpc(EndPoint endPoint) => endPoint is null
		? null
		: new Proto.EndPoint
		{
			Address = endPoint.GetHost(),
			Port = checked((uint)endPoint.GetPort())
		};

	private static EndPoint FromGrpc(Proto.EndPoint endPoint) => endPoint is null
		? null
		: new DnsEndPoint(endPoint.Address, checked((int)endPoint.Port));

	private static Proto.OperationResult ToGrpc(OperationResult result) =>
		Enum.IsDefined(result)
			? (Proto.OperationResult)result
			: throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown operation result");

	private static OperationResult FromGrpc(Proto.OperationResult result) =>
		Enum.IsDefined(result) && Enum.IsDefined((OperationResult)result)
			? (OperationResult)result
			: throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown operation result");

	private static Proto.NotHandledReason ToGrpc(ClientMessage.NotHandled.Types.NotHandledReason reason) =>
		reason switch
		{
			ClientMessage.NotHandled.Types.NotHandledReason.NotReady => Proto.NotHandledReason.NotReady,
			ClientMessage.NotHandled.Types.NotHandledReason.TooBusy => Proto.NotHandledReason.TooBusy,
			ClientMessage.NotHandled.Types.NotHandledReason.NotLeader => Proto.NotHandledReason.NotLeader,
			ClientMessage.NotHandled.Types.NotHandledReason.IsReadOnly => Proto.NotHandledReason.IsReadOnly,
			_ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown not-handled reason")
		};

	private static ClientMessage.NotHandled.Types.NotHandledReason FromGrpc(Proto.NotHandledReason reason) =>
		reason switch
		{
			Proto.NotHandledReason.NotReady => ClientMessage.NotHandled.Types.NotHandledReason.NotReady,
			Proto.NotHandledReason.TooBusy => ClientMessage.NotHandled.Types.NotHandledReason.TooBusy,
			Proto.NotHandledReason.NotLeader => ClientMessage.NotHandled.Types.NotHandledReason.NotLeader,
			Proto.NotHandledReason.IsReadOnly => ClientMessage.NotHandled.Types.NotHandledReason.IsReadOnly,
			_ => ClientMessage.NotHandled.Types.NotHandledReason.NotReady
		};
}
