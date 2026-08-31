using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using EventStore.Core.Authentication.DelegatedAuthentication;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Services.Transport.Grpc.Forwarding;
using EventStore.Core.Services.UserManagement;
using Google.Protobuf;
using NUnit.Framework;
using Proto = EventStore.Forwarding;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Forwarding;

[TestFixture]
public class ForwardingGrpcCodecTests
{
	private static readonly ClaimsPrincipal Anonymous = new();
	private static readonly IReadOnlyDictionary<string, string> NoTokens =
		new Dictionary<string, string>();

	[Test]
	public void forward_is_bidirectional_streaming()
	{
		var method = Proto.RequestForwarding.Descriptor.Methods.Single();

		Assert.Multiple(() =>
		{
			Assert.That(method.Name, Is.EqualTo("Forward"));
			Assert.That(method.IsClientStreaming, Is.True);
			Assert.That(method.IsServerStreaming, Is.True);
		});
	}

	[Test]
	public void open_session_round_trips()
	{
		var session = new ForwardingSession(
			Guid.NewGuid(),
			Guid.NewGuid(),
			new ForwardingSessionGeneration(42));

		var frame = RoundTrip(ForwardingGrpcCodec.ToGrpc(session));
		var decoded = ForwardingGrpcCodec.FromGrpc(frame.Open);

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.FollowerFrame.PayloadOneofCase.Open));
			Assert.That(decoded, Is.EqualTo(session));
		});
	}

	[TestCase(false)]
	[TestCase(true)]
	public void write_events_round_trips_with_property_metadata(bool requireLeader)
	{
		var @event = CreateEvent(isPropertyMetadata: true);
		var message = new ClientMessage.WriteEvents(
			Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, requireLeader,
			"stream", ExpectedVersion.NoStream, [@event], Anonymous);

		var (frame, decoded) = RoundTripRequest<ClientMessage.WriteEvents>(message);

		Assert.Multiple(() =>
		{
			Assert.That(frame.Request.PayloadCase,
				Is.EqualTo(Proto.ForwardRequest.PayloadOneofCase.WriteEvents));
			Assert.That(decoded.CorrelationId, Is.EqualTo(message.InternalCorrId));
			Assert.That(decoded.InternalCorrId, Is.Not.EqualTo(message.InternalCorrId));
			Assert.That(decoded.EventStreamId, Is.EqualTo(message.EventStreamId));
			Assert.That(decoded.ExpectedVersion, Is.EqualTo(message.ExpectedVersion));
			Assert.That(frame.Request.WriteEvents.RequireLeader, Is.EqualTo(message.RequireLeader));
			Assert.That(decoded.RequireLeader, Is.EqualTo(message.RequireLeader));
			AssertEvent(decoded.Events.Single(), @event);
		});
	}

	[TestCase(false)]
	[TestCase(true)]
	public void transaction_start_round_trips(bool requireLeader)
	{
		var message = new ClientMessage.TransactionStart(
			Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, requireLeader,
			"stream", ExpectedVersion.Any, Anonymous);

		var (frame, decoded) = RoundTripRequest<ClientMessage.TransactionStart>(message);

		Assert.Multiple(() =>
		{
			Assert.That(frame.Request.PayloadCase,
				Is.EqualTo(Proto.ForwardRequest.PayloadOneofCase.TransactionStart));
			Assert.That(decoded.CorrelationId, Is.EqualTo(message.InternalCorrId));
			Assert.That(decoded.EventStreamId, Is.EqualTo(message.EventStreamId));
			Assert.That(decoded.ExpectedVersion, Is.EqualTo(message.ExpectedVersion));
			Assert.That(frame.Request.TransactionStart.RequireLeader, Is.EqualTo(message.RequireLeader));
			Assert.That(decoded.RequireLeader, Is.EqualTo(message.RequireLeader));
		});
	}

	[TestCase(false)]
	[TestCase(true)]
	public void transaction_write_round_trips(bool requireLeader)
	{
		var @event = CreateEvent(isPropertyMetadata: true);
		var message = new ClientMessage.TransactionWrite(
			Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, requireLeader, 42, [@event], Anonymous);

		var (frame, decoded) = RoundTripRequest<ClientMessage.TransactionWrite>(message);

		Assert.Multiple(() =>
		{
			Assert.That(frame.Request.PayloadCase,
				Is.EqualTo(Proto.ForwardRequest.PayloadOneofCase.TransactionWrite));
			Assert.That(decoded.CorrelationId, Is.EqualTo(message.InternalCorrId));
			Assert.That(decoded.TransactionId, Is.EqualTo(message.TransactionId));
			Assert.That(frame.Request.TransactionWrite.RequireLeader, Is.EqualTo(message.RequireLeader));
			Assert.That(decoded.RequireLeader, Is.EqualTo(message.RequireLeader));
			AssertEvent(decoded.Events.Single(), @event);
		});
	}

	[TestCase(false)]
	[TestCase(true)]
	public void transaction_commit_round_trips(bool requireLeader)
	{
		var message = new ClientMessage.TransactionCommit(
			Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, requireLeader, 42, Anonymous);

		var (frame, decoded) = RoundTripRequest<ClientMessage.TransactionCommit>(message);

		Assert.Multiple(() =>
		{
			Assert.That(frame.Request.PayloadCase,
				Is.EqualTo(Proto.ForwardRequest.PayloadOneofCase.TransactionCommit));
			Assert.That(decoded.CorrelationId, Is.EqualTo(message.InternalCorrId));
			Assert.That(decoded.TransactionId, Is.EqualTo(message.TransactionId));
			Assert.That(frame.Request.TransactionCommit.RequireLeader, Is.EqualTo(message.RequireLeader));
			Assert.That(decoded.RequireLeader, Is.EqualTo(message.RequireLeader));
		});
	}

	[TestCase(false)]
	[TestCase(true)]
	public void delete_stream_round_trips(bool requireLeader)
	{
		var message = new ClientMessage.DeleteStream(
			Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, requireLeader,
			"stream", ExpectedVersion.StreamExists, true, Anonymous);

		var (frame, decoded) = RoundTripRequest<ClientMessage.DeleteStream>(message);

		Assert.Multiple(() =>
		{
			Assert.That(frame.Request.PayloadCase,
				Is.EqualTo(Proto.ForwardRequest.PayloadOneofCase.DeleteStream));
			Assert.That(decoded.CorrelationId, Is.EqualTo(message.InternalCorrId));
			Assert.That(decoded.EventStreamId, Is.EqualTo(message.EventStreamId));
			Assert.That(decoded.ExpectedVersion, Is.EqualTo(message.ExpectedVersion));
			Assert.That(decoded.HardDelete, Is.EqualTo(message.HardDelete));
			Assert.That(frame.Request.DeleteStream.RequireLeader, Is.EqualTo(message.RequireLeader));
			Assert.That(decoded.RequireLeader, Is.EqualTo(message.RequireLeader));
		});
	}

	[Test]
	public void all_decoded_requests_preserve_the_stream_cancellation_token()
	{
		using var cancellation = new CancellationTokenSource();
		var @event = CreateEvent(isPropertyMetadata: false);
		ClientMessage.WriteRequestMessage[] requests =
		[
			new ClientMessage.WriteEvents(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				"stream", ExpectedVersion.Any, [@event], Anonymous),
			new ClientMessage.TransactionStart(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				"stream", ExpectedVersion.Any, Anonymous),
			new ClientMessage.TransactionWrite(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				42, [@event], Anonymous),
			new ClientMessage.TransactionCommit(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				42, Anonymous),
			new ClientMessage.DeleteStream(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				"stream", ExpectedVersion.Any, false, Anonymous)
		];

		var decoded = requests.Select(request => ForwardingGrpcCodec.FromGrpc(
			RoundTrip(ForwardingGrpcCodec.ToGrpc(request, ForwardingTransportSecurity.Cleartext)).Request,
			IEnvelope.NoOp,
			request.User,
			request.Tokens ?? NoTokens,
			cancellation.Token));

		Assert.That(decoded.Select(message => message.CancellationToken),
			Is.All.EqualTo(cancellation.Token));
	}

	[Test]
	public void trusted_system_identity_round_trips()
	{
		var message = CreateTransactionStart(SystemAccounts.System);

		var identity = RoundTripIdentity(message);

		Assert.That(identity, Is.TypeOf<ForwardingIdentity.TrustedSystem>());
	}

	[Test]
	public void delegated_bearer_identity_round_trips()
	{
		var user = new ClaimsPrincipal(new DelegatedClaimsIdentity(
			new Dictionary<string, string> { ["jwt"] = "token" }));
		var message = CreateTransactionStart(user);

		var identity = (ForwardingIdentity.BearerToken)RoundTripIdentity(message);

		Assert.That(identity.Token, Is.EqualTo("token"));
	}

	[Test]
	public void delegated_bearer_identity_requires_explicit_tls_transport()
	{
		var user = new ClaimsPrincipal(new DelegatedClaimsIdentity(
			new Dictionary<string, string> { ["jwt"] = "token" }));
		var message = CreateTransactionStart(user);

		Assert.That(() => ForwardingGrpcCodec.ToGrpc(message, ForwardingTransportSecurity.Cleartext),
			Throws.InvalidOperationException);
	}

	[Test]
	public void user_password_identity_round_trips()
	{
		var tokens = new Dictionary<string, string> { ["uid"] = "admin", ["pwd"] = "changeit" };
		var message = CreateTransactionStart(Anonymous, tokens);

		var identity = (ForwardingIdentity.UserPassword)RoundTripIdentity(message);

		Assert.Multiple(() =>
		{
			Assert.That(identity.Username, Is.EqualTo("admin"));
			Assert.That(identity.Password, Is.EqualTo("changeit"));
		});
	}

	[Test]
	public void user_password_identity_requires_explicit_tls_transport()
	{
		var tokens = new Dictionary<string, string> { ["uid"] = "admin", ["pwd"] = "changeit" };
		var message = CreateTransactionStart(Anonymous, tokens);

		Assert.That(() => ForwardingGrpcCodec.ToGrpc(message, ForwardingTransportSecurity.Cleartext),
			Throws.InvalidOperationException);
	}

	[Test]
	public void anonymous_identity_round_trips()
	{
		var message = CreateTransactionStart(Anonymous);

		var identity = RoundTripIdentity(message);

		Assert.That(identity, Is.TypeOf<ForwardingIdentity.Anonymous>());
	}

	[Test]
	public void write_events_completion_round_trips_consistency_failures()
	{
		var correlationId = Guid.NewGuid();
		var failures = new[]
		{
			new ConsistencyCheckFailure(0, 10, 11, true),
			new ConsistencyCheckFailure(1, 20, 21, null)
		};
		var message = new ClientMessage.WriteEventsCompleted(
			correlationId, OperationResult.WrongExpectedVersion, "wrong version", 21, failures);

		var decoded = RoundTripResponse<ClientMessage.WriteEventsCompleted>(message);

		Assert.Multiple(() =>
		{
			Assert.That(decoded.CorrelationId, Is.EqualTo(correlationId));
			Assert.That(decoded.Result, Is.EqualTo(message.Result));
			Assert.That(decoded.Message, Is.EqualTo(message.Message));
			Assert.That(decoded.CurrentVersion, Is.EqualTo(message.CurrentVersion));
			Assert.That(decoded.ConsistencyCheckFailures, Is.EqualTo(failures));
		});
	}

	[Test]
	public void transaction_start_completion_round_trips()
	{
		var message = new ClientMessage.TransactionStartCompleted(
			Guid.NewGuid(), 42, OperationResult.Success, "started");

		var decoded = RoundTripResponse<ClientMessage.TransactionStartCompleted>(message);

		Assert.Multiple(() =>
		{
			Assert.That(decoded.CorrelationId, Is.EqualTo(message.CorrelationId));
			Assert.That(decoded.TransactionId, Is.EqualTo(message.TransactionId));
			Assert.That(decoded.Result, Is.EqualTo(message.Result));
			Assert.That(decoded.Message, Is.EqualTo(message.Message));
		});
	}

	[Test]
	public void transaction_write_completion_round_trips()
	{
		var message = new ClientMessage.TransactionWriteCompleted(
			Guid.NewGuid(), 42, OperationResult.InvalidTransaction, "invalid transaction");

		var decoded = RoundTripResponse<ClientMessage.TransactionWriteCompleted>(message);

		Assert.Multiple(() =>
		{
			Assert.That(decoded.CorrelationId, Is.EqualTo(message.CorrelationId));
			Assert.That(decoded.TransactionId, Is.EqualTo(message.TransactionId));
			Assert.That(decoded.Result, Is.EqualTo(message.Result));
			Assert.That(decoded.Message, Is.EqualTo(message.Message));
		});
	}

	[Test]
	public void transaction_commit_completion_round_trips_positions()
	{
		var message = new ClientMessage.TransactionCommitCompleted(
			Guid.NewGuid(), 42, 10, 12, 1_000, 1_100);

		var decoded = RoundTripResponse<ClientMessage.TransactionCommitCompleted>(message);

		Assert.Multiple(() =>
		{
			Assert.That(decoded.CorrelationId, Is.EqualTo(message.CorrelationId));
			Assert.That(decoded.TransactionId, Is.EqualTo(message.TransactionId));
			Assert.That(decoded.Result, Is.EqualTo(OperationResult.Success));
			Assert.That(decoded.FirstEventNumber, Is.EqualTo(message.FirstEventNumber));
			Assert.That(decoded.LastEventNumber, Is.EqualTo(message.LastEventNumber));
			Assert.That(decoded.PreparePosition, Is.EqualTo(message.PreparePosition));
			Assert.That(decoded.CommitPosition, Is.EqualTo(message.CommitPosition));
		});
	}

	[Test]
	public void delete_stream_completion_round_trips_consistency_failures()
	{
		var failures = new[] { new ConsistencyCheckFailure(0, 10, 11, false) };
		var message = new ClientMessage.DeleteStreamCompleted(
			Guid.NewGuid(), OperationResult.WrongExpectedVersion, "wrong version",
			11, 1_000, 1_100, failures);

		var decoded = RoundTripResponse<ClientMessage.DeleteStreamCompleted>(message);

		Assert.Multiple(() =>
		{
			Assert.That(decoded.CorrelationId, Is.EqualTo(message.CorrelationId));
			Assert.That(decoded.Result, Is.EqualTo(message.Result));
			Assert.That(decoded.Message, Is.EqualTo(message.Message));
			Assert.That(decoded.CurrentVersion, Is.EqualTo(message.CurrentVersion));
			Assert.That(decoded.PreparePosition, Is.EqualTo(message.PreparePosition));
			Assert.That(decoded.CommitPosition, Is.EqualTo(message.CommitPosition));
			Assert.That(decoded.ConsistencyCheckFailures, Is.EqualTo(failures));
		});
	}

	[Test]
	public void not_handled_description_round_trips()
	{
		var message = new ClientMessage.NotHandled(
			Guid.NewGuid(), ClientMessage.NotHandled.Types.NotHandledReason.TooBusy, "busy");

		var decoded = RoundTripResponse<ClientMessage.NotHandled>(message);

		Assert.Multiple(() =>
		{
			Assert.That(decoded.CorrelationId, Is.EqualTo(message.CorrelationId));
			Assert.That(decoded.Reason, Is.EqualTo(message.Reason));
			Assert.That(decoded.Description, Is.EqualTo(message.Description));
			Assert.That(decoded.LeaderInfo, Is.Null);
		});
	}

	[Test]
	public void not_handled_leader_info_round_trips()
	{
		var message = new ClientMessage.NotHandled(
			Guid.NewGuid(),
			ClientMessage.NotHandled.Types.NotHandledReason.NotLeader,
			new ClientMessage.NotHandled.Types.LeaderInfo(
				new DnsEndPoint("leader-tcp.internal", 1113),
				true,
				new DnsEndPoint("leader-http.internal", 2113)));

		var decoded = RoundTripResponse<ClientMessage.NotHandled>(message);

		Assert.Multiple(() =>
		{
			Assert.That(decoded.CorrelationId, Is.EqualTo(message.CorrelationId));
			Assert.That(decoded.Reason, Is.EqualTo(message.Reason));
			Assert.That(decoded.LeaderInfo.IsSecure, Is.True);
			Assert.That(decoded.LeaderInfo.ExternalTcp, Is.EqualTo(message.LeaderInfo.ExternalTcp));
			Assert.That(decoded.LeaderInfo.Http, Is.EqualTo(message.LeaderInfo.Http));
		});
	}

	[Test]
	public void not_handled_leader_info_without_external_tcp_round_trips_as_null()
	{
		var message = new ClientMessage.NotHandled(
			Guid.NewGuid(),
			ClientMessage.NotHandled.Types.NotHandledReason.NotLeader,
			new ClientMessage.NotHandled.Types.LeaderInfo(
				null,
				false,
				new DnsEndPoint("leader-http.internal", 2113)));

		var decoded = RoundTripResponse<ClientMessage.NotHandled>(message);

		Assert.Multiple(() =>
		{
			Assert.That(decoded.LeaderInfo.ExternalTcp, Is.Null);
			Assert.That(decoded.LeaderInfo.Http, Is.EqualTo(message.LeaderInfo.Http));
		});
	}

	[Test]
	public void not_authenticated_round_trips()
	{
		var message = new TcpMessage.NotAuthenticated(Guid.NewGuid(), "not authenticated");

		var decoded = RoundTripResponse<TcpMessage.NotAuthenticated>(message);

		Assert.Multiple(() =>
		{
			Assert.That(decoded.CorrelationId, Is.EqualTo(message.CorrelationId));
			Assert.That(decoded.Reason, Is.EqualTo(message.Reason));
		});
	}

	[Test]
	public void unknown_not_handled_reason_maps_to_not_ready()
	{
		var correlationId = Guid.NewGuid();
		var frame = new Proto.LeaderFrame
		{
			Response = new Proto.ForwardResponse
			{
				RequestId = Uuid.FromGuid(correlationId).ToDto(),
				NotHandled = new Proto.NotHandled
				{
					Reason = (Proto.NotHandledReason)int.MaxValue,
					Description = "unknown"
				}
			}
		};

		var decoded = (ClientMessage.NotHandled)ForwardingGrpcCodec.FromGrpc(RoundTrip(frame));

		Assert.That(decoded.Reason,
			Is.EqualTo(ClientMessage.NotHandled.Types.NotHandledReason.NotReady));
	}

	private static (Proto.FollowerFrame Frame, TMessage Message) RoundTripRequest<TMessage>(
		ClientMessage.WriteRequestMessage message)
		where TMessage : ClientMessage.WriteRequestMessage
	{
		var frame = RoundTrip(ForwardingGrpcCodec.ToGrpc(message, ForwardingTransportSecurity.Tls));
		var decoded = ForwardingGrpcCodec.FromGrpc(
			frame.Request, IEnvelope.NoOp, message.User, message.Tokens ?? NoTokens);
		return (frame, (TMessage)decoded);
	}

	private static TMessage RoundTripResponse<TMessage>(Message message)
		where TMessage : Message =>
		(TMessage)ForwardingGrpcCodec.FromGrpc(RoundTrip(ForwardingGrpcCodec.ToGrpc(message)));

	private static ForwardingIdentity RoundTripIdentity(ClientMessage.WriteRequestMessage message)
	{
		var frame = RoundTrip(ForwardingGrpcCodec.ToGrpc(message, ForwardingTransportSecurity.Tls));
		return ForwardingGrpcCodec.GetIdentity(frame.Request.Identity);
	}

	private static ClientMessage.TransactionStart CreateTransactionStart(
		ClaimsPrincipal user,
		IReadOnlyDictionary<string, string> tokens = null) => new(
		Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
		"stream", ExpectedVersion.Any, user, tokens);

	private static Event CreateEvent(bool isPropertyMetadata) => new(
		Guid.NewGuid(), "event-type", true, [1, 2, 3], isPropertyMetadata, [4, 5, 6]);

	private static void AssertEvent(Event actual, Event expected)
	{
		Assert.Multiple(() =>
		{
			Assert.That(actual.EventId, Is.EqualTo(expected.EventId));
			Assert.That(actual.EventType, Is.EqualTo(expected.EventType));
			Assert.That(actual.IsJson, Is.EqualTo(expected.IsJson));
			Assert.That(actual.Data, Is.EqualTo(expected.Data));
			Assert.That(actual.Metadata, Is.EqualTo(expected.Metadata));
			Assert.That(actual.IsPropertyMetadata, Is.EqualTo(expected.IsPropertyMetadata));
		});
	}

	private static Proto.FollowerFrame RoundTrip(Proto.FollowerFrame frame) =>
		Proto.FollowerFrame.Parser.ParseFrom(frame.ToByteArray());

	private static Proto.LeaderFrame RoundTrip(Proto.LeaderFrame frame) =>
		Proto.LeaderFrame.Parser.ParseFrom(frame.ToByteArray());
}
