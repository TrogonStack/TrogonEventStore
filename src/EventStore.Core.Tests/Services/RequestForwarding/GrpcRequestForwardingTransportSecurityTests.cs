using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Core.Authentication.DelegatedAuthentication;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.RequestForwarding;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Services.Transport.Grpc.Forwarding;
using EventStore.Core.Services.Transport.Http.NodeHttpClientFactory;
using NUnit.Framework;
using Proto = EventStore.Forwarding;

namespace EventStore.Core.Tests.Services.RequestForwarding;

[TestFixture]
public class GrpcRequestForwardingTransportSecurityTests
{
	[TestCase("http", ForwardingTransportSecurity.Cleartext)]
	[TestCase("https", ForwardingTransportSecurity.Tls)]
	public void forwarding_transport_security_follows_the_channel_scheme(
		string uriScheme,
		ForwardingTransportSecurity expected)
	{
		var factory = new RequestForwardingGrpcClientFactory(uriScheme, new UnusedNodeHttpClientFactory());

		Assert.That(factory.TransportSecurity, Is.EqualTo(expected));
	}

	[Test]
	public async Task cleartext_credential_rejection_completes_the_proxy_correlation_locally()
	{
		var publisher = new CapturingPublisher();
		var factory = new RejectingServiceFactory();
		await using var supervisor = new GrpcRequestForwardingSupervisor(
			publisher,
			factory,
			_ => { },
			TimeSpan.Zero);
		var leader = CreateLeader();
		supervisor.Handle(new SystemMessage.BecomePreReplica(Guid.NewGuid(), Guid.NewGuid(), leader));
		var request = CreateRequest(new ClaimsPrincipal(new DelegatedClaimsIdentity(
			new Dictionary<string, string> { ["jwt"] = "token" })));

		supervisor.Handle(new ClientMessage.ForwardMessage(request));

		var response = publisher.Messages.OfType<TcpMessage.NotAuthenticated>().Single();
		Assert.That(response.CorrelationId, Is.EqualTo(request.InternalCorrId));
	}

	[Test]
	public async Task cleartext_credentials_are_rejected_without_closing_the_stream()
	{
		var call = new FakeCall();
		var service = new GrpcRequestForwardingService(
			_ => true,
			_ => { },
			new FakeClient(call),
			Guid.NewGuid(),
			new DnsEndPoint("leader.internal", 2113),
			4,
			new ForwardingSessionGeneration(1),
			ForwardingTransportSecurity.Cleartext);
		var completion = service.Start();

		try
		{
			await call.WaitForWrites(1);
			var credentialRequest = CreateRequest(new ClaimsPrincipal(new DelegatedClaimsIdentity(
				new Dictionary<string, string> { ["jwt"] = "token" })));
			var anonymousRequest = CreateRequest(new ClaimsPrincipal());

			var credentialAdmission = service.TryForward(credentialRequest);
			var anonymousAdmission = service.TryForward(anonymousRequest);
			await call.WaitForWrites(2);

			Assert.Multiple(() =>
			{
				Assert.That(credentialAdmission,
					Is.EqualTo(RequestForwardingAdmission.CredentialsRequireTls));
				Assert.That(anonymousAdmission, Is.EqualTo(RequestForwardingAdmission.Accepted));
				Assert.That(call.Writes.Select(frame => frame.PayloadCase), Is.EqualTo(new[]
				{
					Proto.FollowerFrame.PayloadOneofCase.Open,
					Proto.FollowerFrame.PayloadOneofCase.Request
				}));
				Assert.That(
					Uuid.FromDto(call.Writes[1].Request.RequestId).ToGuid(),
					Is.EqualTo(anonymousRequest.InternalCorrId));
			});
		}
		finally
		{
			service.Stop();
			await completion.WaitAsync(TimeSpan.FromSeconds(5));
		}
	}

	private static ClientMessage.TransactionStart CreateRequest(ClaimsPrincipal user) => new(
		Guid.NewGuid(),
		Guid.NewGuid(),
		IEnvelope.NoOp,
		false,
		"stream",
		ExpectedVersion.Any,
		user);

	private static MemberInfo CreateLeader() => MemberInfo.ForVNode(
		Guid.NewGuid(),
		DateTime.UtcNow,
		VNodeState.Leader,
		true,
		new DnsEndPoint("leader-replication.internal", 1112),
		new DnsEndPoint("leader-replication.internal", 1113),
		null,
		null,
		new DnsEndPoint("leader.internal", 2113),
		null,
		0,
		0,
		0,
		0,
		0,
		0,
		0,
		Guid.NewGuid(),
		0,
		false);

	private sealed class RejectingServiceFactory : IGrpcRequestForwardingServiceFactory
	{
		public IGrpcRequestForwardingService Create(
			TryPublishForwardingResponse tryPublishResponse,
			Action<ClientMessage.NotHandled> publishLocalFailure,
			EndPoint leaderEndPoint,
			ForwardingSessionGeneration sessionGeneration) => new RejectingService();
	}

	private sealed class RejectingService : IGrpcRequestForwardingService
	{
		private readonly TaskCompletionSource _completion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task Task => _completion.Task;
		public Task Start() => Task;
		public RequestForwardingAdmission TryForward(ClientMessage.WriteRequestMessage message) =>
			RequestForwardingAdmission.CredentialsRequireTls;
		public void Stop() => _completion.TrySetResult();
	}

	private sealed class CapturingPublisher : IPublisher
	{
		private readonly List<Message> _messages = new();
		public IReadOnlyList<Message> Messages => _messages;
		public void Publish(Message message) => _messages.Add(message);
	}

	private sealed class UnusedNodeHttpClientFactory : INodeHttpClientFactory
	{
		public HttpClient CreateHttpClient(
			string[] additionalCertificateNames,
			Action<SocketsHttpHandler> configureSocketsHttpHandler = null) =>
			throw new InvalidOperationException("The test does not create a forwarding client.");
	}

	private sealed class FakeClient(FakeCall call) : IRequestForwardingGrpcClient
	{
		public IRequestForwardingGrpcCall Forward(CancellationToken cancellationToken)
		{
			call.CancellationToken = cancellationToken;
			return call;
		}

		public void Dispose()
		{
		}
	}

	private sealed class FakeCall : IRequestForwardingGrpcCall
	{
		private readonly SemaphoreSlim _writeCountChanged = new(0);
		private readonly object _sync = new();

		public List<Proto.FollowerFrame> Writes { get; } = new();
		public CancellationToken CancellationToken { get; set; }

		public Task WriteAsync(Proto.FollowerFrame frame)
		{
			lock (_sync)
			{
				Writes.Add(frame);
			}

			_writeCountChanged.Release();
			return Task.CompletedTask;
		}

		public Task CompleteRequestAsync() => Task.CompletedTask;

		public async IAsyncEnumerable<Proto.LeaderFrame> ReadAllAsync(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			yield break;
		}

		public async Task WaitForWrites(int count)
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			while (true)
			{
				lock (_sync)
				{
					if (Writes.Count >= count)
					{
						return;
					}
				}

				await _writeCountChanged.WaitAsync(timeout.Token);
			}
		}

		public void Dispose()
		{
		}
	}
}
