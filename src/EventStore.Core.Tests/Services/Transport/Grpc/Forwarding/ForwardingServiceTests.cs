#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Authentication.DelegatedAuthentication;
using EventStore.Core.Authorization;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Services.Transport.Grpc.Forwarding;
using EventStore.Core.Services.UserManagement;
using EventStore.Plugins.Authentication;
using EventStore.Plugins.Authorization;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Proto = EventStore.Forwarding;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Forwarding;

[TestFixture]
public class ForwardingServiceTests
{
	[Test]
	public void access_is_checked_before_reading_the_session()
	{
		var reader = new EnumerableStreamReader<Proto.FollowerFrame>([]);
		var service = new ForwardingService(
			new CapturingPublisher(),
			new DenyingAuthorizationProvider(),
			new RecordingAuthenticationProvider());

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Forward(
			reader,
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.PermissionDenied));
			Assert.That(reader.MoveNextCount, Is.Zero);
		});
	}

	[Test]
	public void access_uses_the_forwarding_connect_operation()
	{
		var authorization = new CapturingAuthorizationProvider();
		var service = new ForwardingService(
			new CapturingPublisher(),
			authorization,
			new RecordingAuthenticationProvider());

		Assert.ThrowsAsync<RpcException>(() => service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(authorization.Operation.Resource, Is.EqualTo("node/forwarding"));
			Assert.That(authorization.Operation.Action, Is.EqualTo("connect"));
		});
	}

	[Test]
	public void secure_forwarding_requires_a_node_certificate_before_reading_the_session()
	{
		var reader = new EnumerableStreamReader<Proto.FollowerFrame>([OpenFrame()]);
		var service = CreateService();

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Forward(
			reader,
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(isHttps: true)));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.Unauthenticated));
			Assert.That(reader.MoveNextCount, Is.Zero);
		});
	}

	[Test]
	public void first_frame_must_open_the_session()
	{
		var service = CreateService();

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([WriteEventsFrame(SystemAccounts.System)]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
	}

	[Test]
	public void a_second_open_frame_is_rejected()
	{
		var service = CreateService();

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([OpenFrame(), OpenFrame()]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
	}

	[Test]
	public void a_request_without_a_domain_payload_is_rejected()
	{
		var publisher = new CapturingPublisher();
		var request = WriteEventsFrame(SystemAccounts.System);
		request.Request.ClearPayload();
		var service = CreateService(publisher);

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([OpenFrame(), request]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
			Assert.That(publisher.Messages, Is.Empty);
		});
	}

	[Test]
	public async Task trusted_system_is_published_locally_and_receives_a_typed_response()
	{
		ClientMessage.WriteEvents? published = null;
		var publisher = new CapturingPublisher(message =>
		{
			published = (ClientMessage.WriteEvents)message;
			published.Envelope.ReplyWith(new ClientMessage.WriteEventsCompleted(
				published.CorrelationId, 1, 1, 100, 100));
		});
		var response = new CapturingStreamWriter<Proto.LeaderFrame>();
		var authentication = new RecordingAuthenticationProvider();
		var service = CreateService(publisher, authentication);
		var request = WriteEventsFrame(SystemAccounts.System);
		var transportedRequestId = Uuid.FromDto(request.Request.RequestId).ToGuid();

		await service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([OpenFrame(), request]),
			response,
			new TestServerCallContext());

		Assert.Multiple(() =>
		{
			Assert.That(published, Is.Not.Null);
			Assert.That(published!.User, Is.SameAs(SystemAccounts.System));
			Assert.That(published.RequireLeader, Is.EqualTo(
				request.Request.WriteEvents.RequireLeader));
			Assert.That(published.CorrelationId, Is.EqualTo(transportedRequestId));
			Assert.That(published.InternalCorrId, Is.Not.EqualTo(transportedRequestId));
			Assert.That(published.CancellationToken.CanBeCanceled, Is.True);
			Assert.That(authentication.CallCount, Is.Zero);
			Assert.That(response.Messages.Single().Response.PayloadCase,
				Is.EqualTo(Proto.ForwardResponse.PayloadOneofCase.WriteEvents));
			Assert.That(
				Uuid.FromDto(response.Messages.Single().Response.RequestId).ToGuid(),
				Is.EqualTo(transportedRequestId));
		});
	}

	[Test]
	public async Task anonymous_identity_is_not_promoted_to_the_node_principal()
	{
		ClientMessage.WriteEvents? published = null;
		var publisher = new CapturingPublisher(message =>
		{
			published = (ClientMessage.WriteEvents)message;
			published.Envelope.ReplyWith(new ClientMessage.WriteEventsCompleted(
				published.CorrelationId, 1, 1, 100, 100));
		});
		var authentication = new RecordingAuthenticationProvider();
		var service = CreateService(publisher, authentication);

		await service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([
				OpenFrame(),
				WriteEventsFrame(SystemAccounts.Anonymous)
			]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext());

		Assert.Multiple(() =>
		{
			Assert.That(published!.User, Is.SameAs(SystemAccounts.Anonymous));
			Assert.That(authentication.CallCount, Is.Zero);
		});
	}

	[TestCase("jwt", "token")]
	[TestCase("uid", "writer")]
	public async Task delegated_credentials_are_authenticated_for_each_request(string credential, string value)
	{
		var tokens = credential == "jwt"
			? new Dictionary<string, string> { ["jwt"] = value }
			: new Dictionary<string, string> { ["uid"] = value, ["pwd"] = "secret" };
		var delegatedUser = new ClaimsPrincipal(new DelegatedClaimsIdentity(tokens));
		var authenticatedUser = new ClaimsPrincipal(new ClaimsIdentity(
			[new Claim(ClaimTypes.Name, "authenticated")],
			"test"));
		ClientMessage.WriteEvents? published = null;
		var publisher = new CapturingPublisher(message =>
		{
			published = (ClientMessage.WriteEvents)message;
			published.Envelope.ReplyWith(new ClientMessage.WriteEventsCompleted(
				published.CorrelationId, 1, 1, 100, 100));
		});
		var authentication = new RecordingAuthenticationProvider(authenticatedUser);
		var service = CreateService(publisher, authentication);

		await service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([
				OpenFrame(),
				WriteEventsFrame(delegatedUser, tokens)
			]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext());

		Assert.Multiple(() =>
		{
			Assert.That(authentication.CallCount, Is.EqualTo(1));
			Assert.That(authentication.Tokens, Is.EqualTo(tokens));
			Assert.That(published!.User, Is.SameAs(authenticatedUser));
			Assert.That(published.Tokens, Is.EqualTo(tokens));
		});
	}

	[TestCase(AuthenticationOutcome.Unauthorized, Proto.ForwardResponse.PayloadOneofCase.NotAuthenticated)]
	[TestCase(AuthenticationOutcome.Error, Proto.ForwardResponse.PayloadOneofCase.NotAuthenticated)]
	[TestCase(AuthenticationOutcome.NotReady, Proto.ForwardResponse.PayloadOneofCase.NotHandled)]
	public async Task failed_delegated_authentication_stays_a_typed_application_response(
		AuthenticationOutcome outcome,
		Proto.ForwardResponse.PayloadOneofCase responseCase)
	{
		var publisher = new CapturingPublisher();
		var authentication = new RecordingAuthenticationProvider(outcome: outcome);
		var response = new CapturingStreamWriter<Proto.LeaderFrame>();
		var service = CreateService(publisher, authentication);
		var tokens = new Dictionary<string, string> { ["jwt"] = "token" };
		var delegatedUser = new ClaimsPrincipal(new DelegatedClaimsIdentity(tokens));

		await service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([
				OpenFrame(),
				WriteEventsFrame(delegatedUser, tokens)
			]),
			response,
			new TestServerCallContext());

		Assert.Multiple(() =>
		{
			Assert.That(publisher.Messages, Is.Empty);
			Assert.That(response.Messages.Single().Response.PayloadCase, Is.EqualTo(responseCase));
		});
	}

	[Test]
	public async Task all_forwarded_write_types_use_leader_local_internal_ids()
	{
		var requests = WriteRequests().ToArray();
		var publisher = new CapturingPublisher(message => ReplySuccess((ClientMessage.WriteRequestMessage)message));
		var response = new CapturingStreamWriter<Proto.LeaderFrame>();
		var service = CreateService(publisher);

		await service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>(
				[OpenFrame(), .. requests.Select(request => ForwardingGrpcCodec.ToGrpc(
					request, ForwardingTransportSecurity.Tls))]),
			response,
			new TestServerCallContext());

		var published = publisher.Messages.Cast<ClientMessage.WriteRequestMessage>().ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(published.Select(x => x.GetType()), Is.EqualTo(requests.Select(x => x.GetType())));
			Assert.That(published.Select(x => x.RequireLeader),
				Is.EqualTo(requests.Select(x => x.RequireLeader)));
			Assert.That(published.Select(x => x.CorrelationId),
				Is.EqualTo(requests.Select(x => x.InternalCorrId)));
			Assert.That(published.All(x => x.InternalCorrId != x.CorrelationId), Is.True);
			Assert.That(response.Messages, Has.Count.EqualTo(5));
		});
	}

	[Test]
	public async Task admission_is_bounded_until_stream_write_completes_and_rejection_is_correlated()
	{
		var first = WriteEventsFrame(SystemAccounts.System);
		var second = WriteEventsFrame(SystemAccounts.System);
		var publisher = new CapturingPublisher(message => ReplySuccess((ClientMessage.WriteRequestMessage)message));
		var responses = new BlockingStreamWriter<Proto.LeaderFrame>();
		var requests = new ChannelStreamReader<Proto.FollowerFrame>();
		requests.Write(OpenFrame());
		requests.Write(first);
		var service = new ForwardingService(
			publisher,
			new CapturingAuthorizationProvider(),
			new RecordingAuthenticationProvider(),
			sessionCapacity: 1);

		var forwarding = service.Forward(requests, responses, new TestServerCallContext());
		await responses.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
		requests.Write(second);
		Assert.That(
			SpinWait.SpinUntil(() => requests.ItemsRead >= 3, TimeSpan.FromSeconds(5)),
			Is.True);
		requests.Complete();
		responses.Release();
		await forwarding.WaitAsync(TimeSpan.FromSeconds(5));

		var rejected = responses.Messages.Single(x =>
			x.Response.PayloadCase == Proto.ForwardResponse.PayloadOneofCase.NotHandled);
		Assert.Multiple(() =>
		{
			Assert.That(publisher.Messages, Has.Count.EqualTo(1));
			Assert.That(rejected.Response.NotHandled.Reason, Is.EqualTo(Proto.NotHandledReason.TooBusy));
			Assert.That(
				Uuid.FromDto(rejected.Response.RequestId).ToGuid(),
				Is.EqualTo(Uuid.FromDto(second.Request.RequestId).ToGuid()));
		});
	}

	[Test]
	public async Task immediate_authentication_replies_wait_for_stream_capacity()
	{
		var tokens = new Dictionary<string, string> { ["jwt"] = "token" };
		var delegatedUser = new ClaimsPrincipal(new DelegatedClaimsIdentity(tokens));
		var authentication = new RecordingAuthenticationProvider(outcome: AuthenticationOutcome.Unauthorized);
		var responses = new BlockingStreamWriter<Proto.LeaderFrame>();
		var requests = new ChannelStreamReader<Proto.FollowerFrame>();
		requests.Write(OpenFrame());
		requests.Write(WriteEventsFrame(delegatedUser, tokens));
		requests.Write(WriteEventsFrame(delegatedUser, tokens));
		requests.Write(WriteEventsFrame(delegatedUser, tokens));
		requests.Complete();
		var service = new ForwardingService(
			new CapturingPublisher(),
			new CapturingAuthorizationProvider(),
			authentication,
			sessionCapacity: 1);

		var forwarding = service.Forward(requests, responses, new TestServerCallContext());
		await responses.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.That(
			SpinWait.SpinUntil(() => authentication.CallCount == 2, TimeSpan.FromSeconds(5)),
			Is.True);
		responses.Release();

		await forwarding.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.That(
			responses.Messages.Select(x => x.Response.PayloadCase),
			Is.EqualTo(Enumerable.Repeat(
				Proto.ForwardResponse.PayloadOneofCase.NotAuthenticated,
				3)));
	}

	[Test]
	public void waiting_immediate_reply_exits_when_the_session_is_cancelled()
	{
		var tokens = new Dictionary<string, string> { ["jwt"] = "token" };
		var delegatedUser = new ClaimsPrincipal(new DelegatedClaimsIdentity(tokens));
		var authentication = new RecordingAuthenticationProvider(outcome: AuthenticationOutcome.Unauthorized);
		var publisher = new CapturingPublisher();
		var requests = new ChannelStreamReader<Proto.FollowerFrame>();
		requests.Write(OpenFrame());
		requests.Write(WriteEventsFrame(SystemAccounts.System));
		requests.Write(WriteEventsFrame(delegatedUser, tokens));
		var context = new TestServerCallContext();
		var service = new ForwardingService(
			publisher,
			new CapturingAuthorizationProvider(),
			authentication,
			sessionCapacity: 1);

		var forwarding = service.Forward(
			requests,
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			context);
		Assert.That(
			SpinWait.SpinUntil(
				() => publisher.Messages.Count == 1 && authentication.CallCount == 1,
				TimeSpan.FromSeconds(5)),
			Is.True);

		context.Cancel();
		Assert.ThrowsAsync<OperationCanceledException>(async () =>
			await forwarding.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Test]
	public async Task newer_session_from_the_same_authenticated_follower_closes_the_previous_session()
	{
		using var certificate = CreateCertificate("node-a");
		var followerInstanceId = Guid.NewGuid();
		var service = CreateService();
		var firstReader = new ChannelStreamReader<Proto.FollowerFrame>();
		firstReader.Write(OpenFrame(followerInstanceId, generation: 1));
		var first = service.Forward(
			firstReader,
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(certificate));
		Assert.That(
			SpinWait.SpinUntil(() => firstReader.MoveNextCount >= 2, TimeSpan.FromSeconds(5)),
			Is.True);

		await service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([OpenFrame(followerInstanceId, generation: 2)]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(certificate));

		var exception = Assert.ThrowsAsync<RpcException>(async () =>
			await first.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.Cancelled));
	}

	[Test]
	public async Task delayed_older_session_cannot_close_a_newer_session()
	{
		using var certificate = CreateCertificate("node-a");
		var followerInstanceId = Guid.NewGuid();
		var service = CreateService();
		var newerReader = new ChannelStreamReader<Proto.FollowerFrame>();
		newerReader.Write(OpenFrame(followerInstanceId, generation: 2));
		var newerContext = new TestServerCallContext(certificate);
		var newer = service.Forward(
			newerReader,
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			newerContext);
		Assert.That(
			SpinWait.SpinUntil(() => newerReader.MoveNextCount >= 2, TimeSpan.FromSeconds(5)),
			Is.True);

		var older = service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([
				OpenFrame(followerInstanceId, generation: 1)
			]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(certificate));

		try
		{
			var exception = Assert.ThrowsAsync<RpcException>(async () =>
				await older.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.Multiple(() =>
			{
				Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.Cancelled));
				Assert.That(newer.IsCompleted, Is.False);
			});
		}
		finally
		{
			newerContext.Cancel();
			try
			{
				await newer.WaitAsync(TimeSpan.FromSeconds(5));
			}
			catch
			{
			}
		}
	}

	[Test]
	public async Task duplicate_session_id_cannot_replace_the_active_session()
	{
		using var certificate = CreateCertificate("node-a");
		var followerInstanceId = Guid.NewGuid();
		var sessionId = Guid.NewGuid();
		var service = CreateService();
		var activeReader = new ChannelStreamReader<Proto.FollowerFrame>();
		activeReader.Write(OpenFrame(followerInstanceId, sessionId, generation: 1));
		var activeContext = new TestServerCallContext(certificate);
		var active = service.Forward(
			activeReader,
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			activeContext);
		Assert.That(
			SpinWait.SpinUntil(() => activeReader.MoveNextCount >= 2, TimeSpan.FromSeconds(5)),
			Is.True);

		try
		{
			var duplicate = service.Forward(
				new EnumerableStreamReader<Proto.FollowerFrame>([
					OpenFrame(followerInstanceId, sessionId, generation: 2)
				]),
				new CapturingStreamWriter<Proto.LeaderFrame>(),
				new TestServerCallContext(certificate));
			var exception = Assert.ThrowsAsync<RpcException>(async () =>
				await duplicate.WaitAsync(TimeSpan.FromSeconds(5)));

			Assert.Multiple(() =>
			{
				Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.Cancelled));
				Assert.That(active.IsCompleted, Is.False);
			});
		}
		finally
		{
			activeContext.Cancel();
			try
			{
				await active.WaitAsync(TimeSpan.FromSeconds(5));
			}
			catch
			{
			}
		}
	}

	[Test]
	public async Task completed_session_retains_its_generation_fence()
	{
		using var certificate = CreateCertificate("node-a");
		var followerInstanceId = Guid.NewGuid();
		var service = CreateService();

		await service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([
				OpenFrame(followerInstanceId, generation: 2)
			]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(certificate));

		var stale = service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([
				OpenFrame(followerInstanceId, generation: 1)
			]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(certificate));
		var exception = Assert.ThrowsAsync<RpcException>(async () =>
			await stale.WaitAsync(TimeSpan.FromSeconds(5)));

		Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.Cancelled));
	}

	[Test]
	public async Task completed_generation_fences_are_evicted_without_evicting_active_sessions()
	{
		using var certificate = CreateCertificate("node-a");
		var activeFollowerInstanceId = Guid.NewGuid();
		var evictableFollowerInstanceId = Guid.NewGuid();
		var service = new ForwardingService(
			new CapturingPublisher(),
			new CapturingAuthorizationProvider(),
			new RecordingAuthenticationProvider(),
			sessionRegistryCapacity: 1);
		var activeReader = new ChannelStreamReader<Proto.FollowerFrame>();
		activeReader.Write(OpenFrame(activeFollowerInstanceId, generation: 2));
		var activeContext = new TestServerCallContext(certificate);
		var active = service.Forward(
			activeReader,
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			activeContext);
		Assert.That(
			SpinWait.SpinUntil(() => activeReader.MoveNextCount >= 2, TimeSpan.FromSeconds(5)),
			Is.True);

		try
		{
			await ForwardCompletedSession(service, certificate, evictableFollowerInstanceId, 2);
			await ForwardCompletedSession(service, certificate, Guid.NewGuid(), 2);
			await ForwardCompletedSession(service, certificate, Guid.NewGuid(), 2);

			Assert.DoesNotThrowAsync(async () =>
				await ForwardCompletedSession(service, certificate, evictableFollowerInstanceId, 1));

			var staleActive = ForwardCompletedSession(service, certificate, activeFollowerInstanceId, 1);
			var exception = Assert.ThrowsAsync<RpcException>(async () =>
				await staleActive.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.Multiple(() =>
			{
				Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.Cancelled));
				Assert.That(active.IsCompleted, Is.False);
			});
		}
		finally
		{
			activeContext.Cancel();
			try
			{
				await active.WaitAsync(TimeSpan.FromSeconds(5));
			}
			catch
			{
			}
		}
	}

	[Test]
	public async Task different_authenticated_follower_identity_cannot_evict_an_existing_session()
	{
		using var firstCertificate = CreateCertificate("node-a");
		using var secondCertificate = CreateCertificate("node-b");
		var followerInstanceId = Guid.NewGuid();
		var service = CreateService();
		var firstReader = new ChannelStreamReader<Proto.FollowerFrame>();
		firstReader.Write(OpenFrame(followerInstanceId));
		var firstContext = new TestServerCallContext(firstCertificate);
		var first = service.Forward(
			firstReader,
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			firstContext);
		Assert.That(
			SpinWait.SpinUntil(() => firstReader.MoveNextCount >= 2, TimeSpan.FromSeconds(5)),
			Is.True);

		await service.Forward(
			new EnumerableStreamReader<Proto.FollowerFrame>([OpenFrame(followerInstanceId)]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(secondCertificate));

		Assert.That(first.IsCompleted, Is.False);
		firstContext.Cancel();
		Assert.ThrowsAsync<OperationCanceledException>(async () =>
			await first.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	private static ForwardingService CreateService(
		CapturingPublisher? publisher = null,
		RecordingAuthenticationProvider? authenticationProvider = null) => new(
		publisher ?? new CapturingPublisher(),
		new CapturingAuthorizationProvider(),
		authenticationProvider ?? new RecordingAuthenticationProvider());

	private static Task ForwardCompletedSession(
		ForwardingService service,
		X509Certificate2 certificate,
		Guid followerInstanceId,
		long generation) => service.Forward(
		new EnumerableStreamReader<Proto.FollowerFrame>([
			OpenFrame(followerInstanceId, generation: generation)
		]),
		new CapturingStreamWriter<Proto.LeaderFrame>(),
		new TestServerCallContext(certificate));

	private static Proto.FollowerFrame OpenFrame(
		Guid? followerInstanceId = null,
		Guid? sessionId = null,
		long generation = 1) =>
		ForwardingGrpcCodec.ToGrpc(new ForwardingSession(
			followerInstanceId ?? Guid.NewGuid(),
			sessionId ?? Guid.NewGuid(),
			new ForwardingSessionGeneration(generation)));

	private static X509Certificate2 CreateCertificate(string commonName)
	{
		using var rsa = RSA.Create(2048);
		var request = new CertificateRequest(
			$"CN={commonName}",
			rsa,
			HashAlgorithmName.SHA256,
			RSASignaturePadding.Pkcs1);
		return request.CreateSelfSigned(
			DateTimeOffset.UtcNow.AddMinutes(-1),
			DateTimeOffset.UtcNow.AddMinutes(1));
	}

	private static Proto.FollowerFrame WriteEventsFrame(
		ClaimsPrincipal user,
		IReadOnlyDictionary<string, string>? tokens = null) => ForwardingGrpcCodec.ToGrpc(
		new ClientMessage.WriteEvents(
			Guid.NewGuid(),
			Guid.NewGuid(),
			new NoopEnvelope(),
			false,
			"stream",
			-2,
			[new Event(Guid.NewGuid(), "type", true, [1], [2])],
			user,
			tokens),
		ForwardingTransportSecurity.Tls);

	private static IEnumerable<ClientMessage.WriteRequestMessage> WriteRequests()
	{
		var user = SystemAccounts.System;
		yield return new ClientMessage.WriteEvents(
			Guid.NewGuid(), Guid.NewGuid(), new NoopEnvelope(), false,
			"stream", -2, [new Event(Guid.NewGuid(), "type", true, [1], [2])], user);
		yield return new ClientMessage.TransactionStart(
			Guid.NewGuid(), Guid.NewGuid(), new NoopEnvelope(), false, "stream", -2, user);
		yield return new ClientMessage.TransactionWrite(
			Guid.NewGuid(), Guid.NewGuid(), new NoopEnvelope(), false, 10,
			[new Event(Guid.NewGuid(), "type", true, [1], [2])], user);
		yield return new ClientMessage.TransactionCommit(
			Guid.NewGuid(), Guid.NewGuid(), new NoopEnvelope(), false, 10, user);
		yield return new ClientMessage.DeleteStream(
			Guid.NewGuid(), Guid.NewGuid(), new NoopEnvelope(), false, "stream", -2, false, user);
	}

	private static void ReplySuccess(ClientMessage.WriteRequestMessage message)
	{
		message.Envelope.ReplyWith<Message>(message switch
		{
			ClientMessage.WriteEvents => new ClientMessage.WriteEventsCompleted(
				message.CorrelationId, 1, 1, 100, 100),
			ClientMessage.TransactionStart => new ClientMessage.TransactionStartCompleted(
				message.CorrelationId, 10, OperationResult.Success, string.Empty),
			ClientMessage.TransactionWrite => new ClientMessage.TransactionWriteCompleted(
				message.CorrelationId, 10, OperationResult.Success, string.Empty),
			ClientMessage.TransactionCommit => new ClientMessage.TransactionCommitCompleted(
				message.CorrelationId, 10, 1, 1, 100, 100),
			ClientMessage.DeleteStream => new ClientMessage.DeleteStreamCompleted(
				message.CorrelationId, OperationResult.Success, string.Empty, -1, 100, 100),
			_ => throw new ArgumentOutOfRangeException(nameof(message))
		});
	}

	public enum AuthenticationOutcome
	{
		Authenticated,
		Unauthorized,
		Error,
		NotReady
	}

	private sealed class RecordingAuthenticationProvider(
		ClaimsPrincipal? principal = null,
		AuthenticationOutcome outcome = AuthenticationOutcome.Authenticated) : AuthenticationProviderBase(name: "test")
	{
		private int _callCount;

		public int CallCount => Volatile.Read(ref _callCount);
		public IReadOnlyDictionary<string, string>? Tokens { get; private set; }

		public override void Authenticate(AuthenticationRequest authenticationRequest)
		{
			Interlocked.Increment(ref _callCount);
			Tokens = authenticationRequest.Tokens;
			switch (outcome)
			{
				case AuthenticationOutcome.Authenticated:
					authenticationRequest.Authenticated(principal ?? SystemAccounts.System);
					break;
				case AuthenticationOutcome.Unauthorized:
					authenticationRequest.Unauthorized();
					break;
				case AuthenticationOutcome.Error:
					authenticationRequest.Error();
					break;
				case AuthenticationOutcome.NotReady:
					authenticationRequest.NotReady();
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}
	}

	private sealed class CapturingPublisher(Action<Message>? onPublish = null) : IPublisher
	{
		public ConcurrentQueue<Message> Messages { get; } = new();

		public void Publish(Message message)
		{
			Messages.Enqueue(message);
			onPublish?.Invoke(message);
		}
	}

	private sealed class DenyingAuthorizationProvider : AuthorizationProviderBase
	{
		public override ValueTask<bool> CheckAccessAsync(
			ClaimsPrincipal principal,
			Operation operation,
			CancellationToken cancellationToken) => ValueTask.FromResult(false);
	}

	private sealed class CapturingAuthorizationProvider : AuthorizationProviderBase
	{
		public Operation Operation { get; private set; }

		public override ValueTask<bool> CheckAccessAsync(
			ClaimsPrincipal principal,
			Operation operation,
			CancellationToken cancellationToken)
		{
			Operation = operation;
			return ValueTask.FromResult(true);
		}
	}

	private sealed class EnumerableStreamReader<T>(IEnumerable<T> values) : IAsyncStreamReader<T>
	{
		private readonly IEnumerator<T> _values = values.GetEnumerator();

		public int MoveNextCount { get; private set; }
		public T Current { get; private set; } = default!;

		public Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			MoveNextCount++;
			if (!_values.MoveNext())
			{
				return Task.FromResult(false);
			}

			Current = _values.Current;
			return Task.FromResult(true);
		}
	}

	private sealed class ChannelStreamReader<T> : IAsyncStreamReader<T>
	{
		private readonly System.Threading.Channels.Channel<T> _values =
			System.Threading.Channels.Channel.CreateUnbounded<T>();
		private int _itemsRead;

		public int MoveNextCount { get; private set; }
		public int ItemsRead => Volatile.Read(ref _itemsRead);
		public T Current { get; private set; } = default!;

		public void Write(T value) => _values.Writer.TryWrite(value);
		public void Complete() => _values.Writer.TryComplete();

		public async Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			MoveNextCount++;
			if (!await _values.Reader.WaitToReadAsync(cancellationToken))
			{
				return false;
			}

			if (!_values.Reader.TryRead(out var current))
			{
				return false;
			}

			Current = current;
			Interlocked.Increment(ref _itemsRead);
			return true;
		}
	}

	private sealed class BlockingStreamWriter<T> : IServerStreamWriter<T>
	{
		private readonly TaskCompletionSource<bool> _writeStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource<bool> _release =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public ConcurrentQueue<T> Messages { get; } = new();
		public Task WriteStarted => _writeStarted.Task;
		public WriteOptions? WriteOptions { get; set; }

		public void Release() => _release.TrySetResult(true);

		public async Task WriteAsync(T message)
		{
			_writeStarted.TrySetResult(true);
			await _release.Task;
			Messages.Enqueue(message);
		}
	}

	private sealed class CapturingStreamWriter<T> : IServerStreamWriter<T>
	{
		public ConcurrentQueue<T> Messages { get; } = new();
		public WriteOptions? WriteOptions { get; set; }

		public Task WriteAsync(T message)
		{
			Messages.Enqueue(message);
			return Task.CompletedTask;
		}
	}

	private sealed class TestServerCallContext : ServerCallContext
	{
		private readonly CancellationTokenSource _cancellation = new();

		public TestServerCallContext(
			X509Certificate2? clientCertificate = null,
			bool isHttps = false)
		{
			var httpContext = new DefaultHttpContext
			{
				User = SystemAccounts.System
			};
			httpContext.Request.Scheme = isHttps || clientCertificate is not null ? "https" : "http";
			httpContext.Connection.ClientCertificate = clientCertificate;
			UserStateCore["__HttpContext"] = httpContext;
		}

		public void Cancel() => _cancellation.Cancel();

		protected override string MethodCore => "/event_store.forwarding.RequestForwarding/Forward";
		protected override string HostCore => "localhost";
		protected override string PeerCore => "ipv4:127.0.0.1:2113";
		protected override DateTime DeadlineCore => DateTime.MaxValue;
		protected override Metadata RequestHeadersCore { get; } = new();
		protected override CancellationToken CancellationTokenCore => _cancellation.Token;
		protected override Metadata ResponseTrailersCore { get; } = new();
		protected override Status StatusCore { get; set; }
		protected override WriteOptions? WriteOptionsCore { get; set; }
		protected override AuthContext AuthContextCore { get; } =
			new(string.Empty, new Dictionary<string, List<AuthProperty>>());
		protected override IDictionary<object, object> UserStateCore { get; } =
			new Dictionary<object, object>();
		protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
		protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
			throw new NotSupportedException();
	}
}
