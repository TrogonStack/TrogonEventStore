using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Core.Authorization;
using EventStore.Core.Bus;
using EventStore.Core.Cluster.Settings;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Replication;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Services.Transport.Grpc.Replication;
using EventStore.Plugins.Authorization;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Proto = EventStore.Replication;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Replication;

[TestFixture]
public class ReplicationServiceTests
{
	[Test]
	public void access_is_checked_before_reading_the_handshake()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new DenyingAuthorizationProvider());

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.PermissionDenied));
			Assert.That(publisher.Messages, Is.Empty);
		});
	}

	[Test]
	public async Task access_uses_the_replication_connect_operation()
	{
		var authorization = new CapturingAuthorizationProvider();
		var service = new ReplicationService(new CapturingPublisher(), authorization);

		await service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([SubscribeFrame()]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext());

		Assert.Multiple(() =>
		{
			Assert.That(authorization.Operation.Resource, Is.EqualTo("node/replication"));
			Assert.That(authorization.Operation.Action, Is.EqualTo("connect"));
		});
	}

	[Test]
	public void an_empty_request_stream_is_rejected()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
			Assert.That(publisher.Messages, Is.Empty);
		});
	}

	[Test]
	public void replication_is_unavailable_on_a_single_node_server()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(
			publisher, new PassthroughAuthorizationProvider(),
			availability: ReplicationAvailability.Unavailable);

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([SubscribeFrame()]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition));
			Assert.That(publisher.Messages, Is.Empty);
		});
	}

	[Test]
	public void acknowledgement_before_subscribe_is_rejected()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var request = new EnumerableStreamReader<Proto.ReplicaFrame>([AcknowledgementFrame()]);

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			request, new CapturingStreamWriter<Proto.LeaderFrame>(), new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
			Assert.That(publisher.Messages, Is.Empty);
		});
	}

	[Test]
	public void a_second_subscribe_frame_is_rejected()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var subscribe = SubscribeFrame();
		var request = new EnumerableStreamReader<Proto.ReplicaFrame>([subscribe, subscribe]);

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			request, new CapturingStreamWriter<Proto.LeaderFrame>(), new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
			Assert.That(publisher.Messages.OfType<ReplicationMessage.ReplicaSubscriptionRequest>().Count(), Is.EqualTo(1));
		});
	}

	[Test]
	public void an_empty_replica_instance_id_is_rejected()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var subscribe = SubscribeFrame();
		subscribe.Subscribe.ReplicaInstanceId = new EventStore.Client.UUID();

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([subscribe]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
			Assert.That(publisher.Messages, Is.Empty);
		});
	}

	[TestCase(0)]
	[TestCase(ReplicationSubscriptionVersions.V_CURRENT + 1)]
	public void an_unsupported_protocol_version_is_rejected(int version)
	{
		var subscribe = SubscribeFrame();
		subscribe.Subscribe.Version = version;

		AssertInvalidSubscribe(subscribe);
	}

	[Test]
	public void a_negative_log_position_is_rejected()
	{
		var subscribe = SubscribeFrame();
		subscribe.Subscribe.LogPosition = -1;

		AssertInvalidSubscribe(subscribe);
	}

	[Test]
	public void a_malformed_leader_id_is_rejected()
	{
		var subscribe = SubscribeFrame();
		subscribe.Subscribe.LeaderId = new EventStore.Client.UUID();

		AssertInvalidSubscribe(subscribe);
	}

	[Test]
	public void a_malformed_chunk_id_is_rejected()
	{
		var subscribe = SubscribeFrame();
		subscribe.Subscribe.ChunkId = new EventStore.Client.UUID();

		AssertInvalidSubscribe(subscribe);
	}

	[Test]
	public void a_negative_epoch_position_is_rejected()
	{
		var subscribe = SubscribeFrame();
		subscribe.Subscribe.LastEpochs.Add(new Proto.Epoch
		{
			EpochPosition = -1,
			EpochNumber = 0,
			EpochId = Uuid.FromGuid(Guid.NewGuid()).ToDto()
		});

		AssertInvalidSubscribe(subscribe);
	}

	[Test]
	public void too_many_last_epochs_are_rejected()
	{
		var subscribe = SubscribeFrame();
		subscribe.Subscribe.LastEpochs.Add(Enumerable.Range(
			0,
			ClusterConsts.SubscriptionLastEpochCount + 1).Select(epochNumber => new Proto.Epoch
			{
				EpochPosition = epochNumber,
				EpochNumber = epochNumber,
				EpochId = Uuid.FromGuid(Guid.NewGuid()).ToDto()
			}));

		AssertInvalidSubscribe(subscribe);
	}

	[Test]
	public void an_empty_advertised_address_is_rejected()
	{
		var subscribe = SubscribeFrame();
		subscribe.Subscribe.AdvertisedEndpoint.Address = string.Empty;

		AssertInvalidSubscribe(subscribe);
	}

	[TestCase(0u)]
	[TestCase(65_536u)]
	public void an_invalid_advertised_port_is_rejected(uint port)
	{
		var subscribe = SubscribeFrame();
		subscribe.Subscribe.AdvertisedEndpoint.Port = port;

		AssertInvalidSubscribe(subscribe);
	}

	[Test]
	public async Task acknowledgement_after_subscribe_is_published_and_the_session_is_cleaned_up()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var subscribe = SubscribeFrame();
		var acknowledgement = AcknowledgementFrame(
			Uuid.FromDto(subscribe.Subscribe.SubscriptionId).ToGuid());

		await service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([subscribe, acknowledgement]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext());

		var subscription = publisher.Messages.OfType<ReplicationMessage.ReplicaSubscriptionRequest>().Single();
		var ack = publisher.Messages.OfType<ReplicationMessage.ReplicaLogPositionAck>().Single();
		Assert.Multiple(() =>
		{
			Assert.That(subscription.Session.ConnectionId, Is.Not.EqualTo(Guid.Empty));
			Assert.That(subscription.Session.IsClosed, Is.True);
			Assert.That(ack.SubscriptionId, Is.EqualTo(Uuid.FromDto(acknowledgement.Acknowledgement.SubscriptionId).ToGuid()));
			Assert.That(ack.ReplicationLogPosition, Is.EqualTo(100));
			Assert.That(ack.WriterLogPosition, Is.EqualTo(90));
			Assert.That(subscription.Session.GetStatistics().TotalBytesReceived, Is.GreaterThan(0));
		});
	}

	[Test]
	public void an_acknowledgement_for_another_subscription_is_rejected()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var subscribe = SubscribeFrame();

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([subscribe, AcknowledgementFrame()]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
			Assert.That(publisher.Messages.OfType<ReplicationMessage.ReplicaLogPositionAck>(), Is.Empty);
		});
	}

	[TestCase(-1, 0)]
	[TestCase(0, -1)]
	[TestCase(90, 100)]
	public void an_acknowledgement_with_invalid_positions_is_rejected(
		long replicationLogPosition,
		long writerLogPosition)
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var subscribe = SubscribeFrame();
		var subscriptionId = Uuid.FromDto(subscribe.Subscribe.SubscriptionId).ToGuid();

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([
				subscribe,
				AcknowledgementFrame(subscriptionId, replicationLogPosition, writerLogPosition)
			]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
			Assert.That(publisher.Messages.OfType<ReplicationMessage.ReplicaLogPositionAck>(), Is.Empty);
		});
	}

	[Test]
	public void a_regressing_acknowledgement_is_rejected()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var subscribe = SubscribeFrame();
		var subscriptionId = Uuid.FromDto(subscribe.Subscribe.SubscriptionId).ToGuid();

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([
				subscribe,
				AcknowledgementFrame(subscriptionId, 100, 90),
				AcknowledgementFrame(subscriptionId, 99, 89)
			]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
			Assert.That(publisher.Messages.OfType<ReplicationMessage.ReplicaLogPositionAck>(), Has.Exactly(1).Items);
		});
	}

	[Test]
	public void a_regressing_writer_acknowledgement_is_rejected()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var subscribe = SubscribeFrame();
		var subscriptionId = Uuid.FromDto(subscribe.Subscribe.SubscriptionId).ToGuid();

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([
				subscribe,
				AcknowledgementFrame(subscriptionId, 100, 90),
				AcknowledgementFrame(subscriptionId, 101, 89)
			]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
			Assert.That(publisher.Messages.OfType<ReplicationMessage.ReplicaLogPositionAck>(), Has.Exactly(1).Items);
		});
	}

	[Test]
	public void a_malformed_acknowledgement_is_rejected()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var acknowledgement = AcknowledgementFrame();
		acknowledgement.Acknowledgement.SubscriptionId = new EventStore.Client.UUID();

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([SubscribeFrame(), acknowledgement]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
			Assert.That(publisher.Messages.OfType<ReplicationMessage.ReplicaLogPositionAck>(), Is.Empty);
		});
	}

	[Test]
	public void rejection_is_a_terminal_failure_and_writes_no_response_frame()
	{
		var response = new CapturingStreamWriter<Proto.LeaderFrame>();
		var publisher = new CapturingPublisher(message =>
		{
			if (message is ReplicationMessage.ReplicaSubscriptionRequest request)
			{
				request.Session.Reject(new ReplicationSessionRejection(request.CorrelationId, "subscription rejected"));
			}
		});
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			new BlockingStreamReader<Proto.ReplicaFrame>(SubscribeFrame()),
			response,
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition));
			Assert.That(exception.Status.Detail, Does.Contain("subscription rejected"));
			Assert.That(response.Messages, Is.Empty);
		});
	}

	[Test]
	public async Task call_cancellation_closes_the_published_session()
	{
		using var cancellation = new CancellationTokenSource();
		var subscribed = new TaskCompletionSource<ReplicationMessage.ReplicaSubscriptionRequest>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var publisher = new CapturingPublisher(message =>
		{
			if (message is ReplicationMessage.ReplicaSubscriptionRequest request)
			{
				subscribed.TrySetResult(request);
			}
		});
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var call = service.Replicate(
			new BlockingStreamReader<Proto.ReplicaFrame>(SubscribeFrame()),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(cancellation.Token));
		var subscription = await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));

		await cancellation.CancelAsync();
		Assert.CatchAsync<OperationCanceledException>(async () => await call);

		Assert.That(subscription.Session.IsClosed, Is.True);
	}

	[Test]
	public async Task reconnect_replaces_the_existing_session_for_the_same_replica()
	{
		using var clientCertificate = CreateCertificate("replica");
		var subscriptions = System.Threading.Channels.Channel
			.CreateUnbounded<ReplicationMessage.ReplicaSubscriptionRequest>();
		var publisher = new CapturingPublisher(message =>
		{
			if (message is ReplicationMessage.ReplicaSubscriptionRequest request)
			{
				subscriptions.Writer.TryWrite(request);
			}
		});
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var replicaInstanceId = Guid.NewGuid();
		var firstCall = service.Replicate(
			new BlockingStreamReader<Proto.ReplicaFrame>(SubscribeFrame(replicaInstanceId)),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(clientCertificate: clientCertificate));
		var first = await subscriptions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

		await service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([SubscribeFrame(replicaInstanceId)]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(clientCertificate: clientCertificate));
		var second = await subscriptions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
		await firstCall.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Multiple(() =>
		{
			Assert.That(first.Session.Identity, Is.EqualTo(second.Session.Identity));
			Assert.That(first.Session.Identity.TransportIdentityKind,
				Is.EqualTo(ReplicationTransportIdentityKind.ClientCertificateSha256));
			Assert.That(first.Session.ConnectionId, Is.Not.EqualTo(second.Session.ConnectionId));
			Assert.That(first.Session.IsClosed, Is.True);
		});
	}

	[Test]
	public async Task tls_disabled_replication_uses_the_insecure_system_identity()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var replicaInstanceId = Guid.NewGuid();

		await service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([SubscribeFrame(replicaInstanceId)]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext());

		var subscription = publisher.Messages
			.OfType<ReplicationMessage.ReplicaSubscriptionRequest>()
			.Single();
		Assert.Multiple(() =>
		{
			Assert.That(subscription.Session.Identity.ReplicaInstanceId, Is.EqualTo(replicaInstanceId));
			Assert.That(subscription.Session.Identity.TransportIdentityKind,
				Is.EqualTo(ReplicationTransportIdentityKind.InsecureSystem));
		});
	}

	[Test]
	public void tls_enabled_replication_without_a_client_certificate_is_rejected()
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([SubscribeFrame()]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(isHttps: true)));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.Unauthenticated));
			Assert.That(publisher.Messages, Is.Empty);
		});
	}

	[Test]
	public async Task different_authenticated_identities_with_the_same_replica_id_coexist()
	{
		using var firstCertificate = CreateCertificate("first-replica");
		using var secondCertificate = CreateCertificate("second-replica");
		using var firstCancellation = new CancellationTokenSource();
		var subscriptions = System.Threading.Channels.Channel
			.CreateUnbounded<ReplicationMessage.ReplicaSubscriptionRequest>();
		var publisher = new CapturingPublisher(message =>
		{
			if (message is ReplicationMessage.ReplicaSubscriptionRequest request)
			{
				subscriptions.Writer.TryWrite(request);
			}
		});
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());
		var replicaInstanceId = Guid.NewGuid();
		var firstCall = service.Replicate(
			new BlockingStreamReader<Proto.ReplicaFrame>(SubscribeFrame(replicaInstanceId)),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(firstCancellation.Token, firstCertificate));
		var first = await subscriptions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

		await service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([SubscribeFrame(replicaInstanceId)]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext(clientCertificate: secondCertificate));
		var second = await subscriptions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Multiple(() =>
		{
			Assert.That(first.Session.Identity, Is.Not.EqualTo(second.Session.Identity));
			Assert.That(first.Session.Identity.ReplicaInstanceId,
				Is.EqualTo(second.Session.Identity.ReplicaInstanceId));
			Assert.That(first.Session.IsClosed, Is.False);
			Assert.That(firstCall.IsCompleted, Is.False);
		});

		await firstCancellation.CancelAsync();
		Assert.CatchAsync<OperationCanceledException>(async () => await firstCall);
	}

	private static Proto.ReplicaFrame SubscribeFrame(Guid replicaInstanceId = default) => ReplicationGrpcCodec.ToGrpc(
		new ReplicationMessage.SubscribeReplica(
			ReplicationSubscriptionVersions.V_CURRENT,
			0,
			Guid.NewGuid(),
			[],
			new System.Net.DnsEndPoint("replica.internal", 2113),
			Guid.NewGuid(),
			Guid.NewGuid(),
			true,
			replicaInstanceId == Guid.Empty ? Guid.NewGuid() : replicaInstanceId));

	private static Proto.ReplicaFrame AcknowledgementFrame(
		Guid subscriptionId = default,
		long replicationLogPosition = 100,
		long writerLogPosition = 90) => ReplicationGrpcCodec.ToGrpc(
		new ReplicationMessage.AckLogPosition(
			subscriptionId == Guid.Empty ? Guid.NewGuid() : subscriptionId,
			replicationLogPosition,
			writerLogPosition));

	private static X509Certificate2 CreateCertificate(string commonName)
	{
		using var key = RSA.Create(2048);
		var request = new CertificateRequest(
			$"CN={commonName}",
			key,
			HashAlgorithmName.SHA256,
			RSASignaturePadding.Pkcs1);
		return request.CreateSelfSigned(
			DateTimeOffset.UtcNow.AddMinutes(-1),
			DateTimeOffset.UtcNow.AddMinutes(1));
	}

	private static void AssertInvalidSubscribe(Proto.ReplicaFrame subscribe)
	{
		var publisher = new CapturingPublisher();
		var service = new ReplicationService(publisher, new PassthroughAuthorizationProvider());

		var exception = Assert.ThrowsAsync<RpcException>(() => service.Replicate(
			new EnumerableStreamReader<Proto.ReplicaFrame>([subscribe]),
			new CapturingStreamWriter<Proto.LeaderFrame>(),
			new TestServerCallContext()));

		Assert.Multiple(() =>
		{
			Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
			Assert.That(publisher.Messages, Is.Empty);
		});
	}

	private sealed class CapturingPublisher(Action<Message> onPublish = null) : IPublisher
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

		public T Current { get; private set; } = default!;

		public Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			if (!_values.MoveNext())
			{
				return Task.FromResult(false);
			}

			Current = _values.Current;
			return Task.FromResult(true);
		}
	}

	private sealed class BlockingStreamReader<T>(T first) : IAsyncStreamReader<T>
	{
		private readonly System.Threading.Channels.Channel<T> _channel = Create(first);

		public T Current { get; private set; } = default!;

		public async Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			Current = await _channel.Reader.ReadAsync(cancellationToken);
			return true;
		}

		private static System.Threading.Channels.Channel<T> Create(T value)
		{
			var channel = System.Threading.Channels.Channel.CreateUnbounded<T>();
			channel.Writer.TryWrite(value);
			return channel;
		}
	}

	private sealed class CapturingStreamWriter<T> : IServerStreamWriter<T>
	{
		public ConcurrentQueue<T> Messages { get; } = new();
		public WriteOptions WriteOptions { get; set; } = null!;

		public Task WriteAsync(T message)
		{
			Messages.Enqueue(message);
			return Task.CompletedTask;
		}
	}

	private sealed class TestServerCallContext : ServerCallContext
	{
		private readonly CancellationToken _cancellationToken;

		public TestServerCallContext(
			CancellationToken cancellationToken = default,
			X509Certificate2 clientCertificate = null,
			bool isHttps = false)
		{
			_cancellationToken = cancellationToken;
			var httpContext = new DefaultHttpContext
			{
				User = new ClaimsPrincipal(new ClaimsIdentity())
			};
			httpContext.Request.Scheme = isHttps || clientCertificate is not null ? "https" : "http";
			httpContext.Connection.ClientCertificate = clientCertificate;
			UserStateCore["__HttpContext"] = httpContext;
		}

		protected override string MethodCore => "/event_store.replication.Replication/Replicate";
		protected override string HostCore => "localhost";
		protected override string PeerCore => "ipv4:127.0.0.1:2113";
		protected override DateTime DeadlineCore => DateTime.MaxValue;
		protected override Metadata RequestHeadersCore { get; } = new();
		protected override CancellationToken CancellationTokenCore => _cancellationToken;
		protected override Metadata ResponseTrailersCore { get; } = new();
		protected override Status StatusCore { get; set; }
		protected override WriteOptions WriteOptionsCore { get; set; } = null!;
		protected override AuthContext AuthContextCore { get; } =
			new(string.Empty, new Dictionary<string, List<AuthProperty>>());
		protected override IDictionary<object, object> UserStateCore { get; } = new Dictionary<object, object>();
		protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
		protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) =>
			throw new NotSupportedException();
	}
}
