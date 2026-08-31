using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.RequestForwarding;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Services.Transport.Grpc.Forwarding;
using EventStore.Core.Services.Transport.Http.NodeHttpClientFactory;
using NUnit.Framework;
using Proto = EventStore.Forwarding;

namespace EventStore.Core.Tests.Services.RequestForwarding;

[TestFixture]
public class RequestForwardingGrpcClientTests
{
	[Test]
	public void forwarding_client_enables_http2_keepalive_for_idle_streams()
	{
		var nodeHttpClientFactory = new RecordingNodeHttpClientFactory();
		var factory = new RequestForwardingGrpcClientFactory(Uri.UriSchemeHttps, nodeHttpClientFactory);

		using var client = factory.Create(new DnsEndPoint("leader.internal", 2113));

		Assert.Multiple(() =>
		{
			Assert.That(nodeHttpClientFactory.Handler.KeepAlivePingDelay, Is.EqualTo(TimeSpan.FromSeconds(10)));
			Assert.That(nodeHttpClientFactory.Handler.KeepAlivePingTimeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
			Assert.That(nodeHttpClientFactory.Handler.KeepAlivePingPolicy, Is.EqualTo(HttpKeepAlivePingPolicy.Always));
		});
	}

	[Test]
	public void forwarding_client_uses_the_configured_keepalive_intervals()
	{
		var pingDelay = TimeSpan.FromSeconds(12);
		var pingTimeout = TimeSpan.FromSeconds(7);
		var nodeHttpClientFactory = new RecordingNodeHttpClientFactory();
		var factory = new RequestForwardingGrpcClientFactory(
			Uri.UriSchemeHttps,
			nodeHttpClientFactory,
			pingDelay,
			pingTimeout);

		using var client = factory.Create(new DnsEndPoint("leader.internal", 2113));

		Assert.Multiple(() =>
		{
			Assert.That(nodeHttpClientFactory.Handler.KeepAlivePingDelay, Is.EqualTo(pingDelay));
			Assert.That(nodeHttpClientFactory.Handler.KeepAlivePingTimeout, Is.EqualTo(pingTimeout));
		});
	}

	private sealed class RecordingNodeHttpClientFactory : INodeHttpClientFactory
	{
		public SocketsHttpHandler Handler { get; private set; }

		public HttpClient CreateHttpClient(
			string[] additionalCertificateNames,
			Action<SocketsHttpHandler> configureSocketsHttpHandler = null)
		{
			Handler = new SocketsHttpHandler();
			configureSocketsHttpHandler?.Invoke(Handler);
			return new HttpClient(Handler);
		}
	}
}

[TestFixture]
public class GrpcRequestForwardingServiceTests
{
	[Test]
	public void default_session_generation_is_rejected()
	{
		Assert.That(() => new GrpcRequestForwardingService(
			_ => true,
			_ => { },
			new FakeClient(new FakeCall()),
			Guid.NewGuid(),
			new DnsEndPoint("leader.internal", 2113),
			4,
			default),
			Throws.TypeOf<ArgumentOutOfRangeException>());
	}

	[Test]
	public async Task opens_the_session_before_forwarding_requests()
	{
		var call = new FakeCall();
		var service = CreateService(call);

		var completion = service.Start();
		await call.WaitForWrites(1);
		var request = CreateWriteRequests().First();
		Assert.That(service.TryForward(request), Is.EqualTo(RequestForwardingAdmission.Accepted));
		await call.WaitForWrites(2);

		Assert.Multiple(() =>
		{
			Assert.That(call.Writes[0].PayloadCase, Is.EqualTo(Proto.FollowerFrame.PayloadOneofCase.Open));
			Assert.That(call.Writes[0].Open.ConnectionGeneration, Is.EqualTo(1));
			Assert.That(call.Writes[1].PayloadCase, Is.EqualTo(Proto.FollowerFrame.PayloadOneofCase.Request));
			Assert.That(
				Uuid.FromDto(call.Writes[1].Request.RequestId).ToGuid(),
				Is.EqualTo(request.InternalCorrId));
		});

		service.Stop();
		await completion;
	}

	[Test]
	public async Task request_admission_is_bounded_and_nonblocking()
	{
		var call = new FakeCall { BlockRequestWrites = true };
		var service = CreateService(call, requestQueueCapacity: 1);
		var completion = service.Start();
		await call.WaitForWrites(1);
		var requests = Enumerable.Range(0, 3).Select(_ => CreateWriteRequests().First()).ToArray();

		Assert.That(service.TryForward(requests[0]), Is.EqualTo(RequestForwardingAdmission.Accepted));
		await call.RequestWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Multiple(() =>
		{
			Assert.That(service.TryForward(requests[1]), Is.EqualTo(RequestForwardingAdmission.Accepted));
			Assert.That(service.TryForward(requests[2]), Is.EqualTo(RequestForwardingAdmission.QueueFull));
		});

		call.ReleaseRequestWrites();
		service.Stop();
		await completion;
	}

	[Test]
	public async Task publishes_correlated_responses_from_the_stream()
	{
		var publisher = new CapturingPublisher();
		var call = new FakeCall();
		var service = CreateService(call, publisher: publisher);
		var completion = service.Start();
		await call.WaitForWrites(1);
		var request = CreateWriteRequests().OfType<ClientMessage.TransactionWrite>().Single();
		Assert.That(service.TryForward(request), Is.EqualTo(RequestForwardingAdmission.Accepted));
		await call.WaitForWrites(2);

		await call.Responses.Writer.WriteAsync(ForwardingGrpcCodec.ToGrpc(
			new ClientMessage.TransactionWriteCompleted(
				request.InternalCorrId,
				request.TransactionId,
				OperationResult.Success,
				null)));
		await WaitUntil(() => publisher.Messages.Count == 1);

		var response = publisher.Messages.Single() as ClientMessage.TransactionWriteCompleted;
		Assert.Multiple(() =>
		{
			Assert.That(response, Is.Not.Null);
			Assert.That(response?.CorrelationId, Is.EqualTo(request.InternalCorrId));
			Assert.That(response?.TransactionId, Is.EqualTo(request.TransactionId));
		});

		service.Stop();
		await completion;
	}

	[Test]
	public async Task a_failed_write_ends_the_stream_without_retrying_the_request()
	{
		var call = new FakeCall { RequestWriteException = new InvalidOperationException("closed") };
		var client = new FakeClient(call);
		var service = CreateService(call, client: client);
		var completion = service.Start();
		await call.WaitForWrites(1);

		Assert.That(service.TryForward(CreateWriteRequests().First()),
			Is.EqualTo(RequestForwardingAdmission.Accepted));
		await completion.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Multiple(() =>
		{
			Assert.That(client.ForwardCalls, Is.EqualTo(1));
			Assert.That(call.Writes.Count(frame => frame.PayloadCase == Proto.FollowerFrame.PayloadOneofCase.Request),
				Is.EqualTo(1));
		});
	}

	[Test]
	public async Task a_failed_write_completes_the_failed_and_queued_requests_as_not_ready()
	{
		var publisher = new CapturingPublisher();
		var call = new FakeCall
		{
			BlockRequestWrites = true,
			RequestWriteException = new InvalidOperationException("closed")
		};
		var service = CreateService(call, publisher);
		var completion = service.Start();
		await call.WaitForWrites(1);
		var requests = CreateWriteRequests().Take(2).ToArray();

		Assert.That(service.TryForward(requests[0]), Is.EqualTo(RequestForwardingAdmission.Accepted));
		await call.RequestWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.That(service.TryForward(requests[1]), Is.EqualTo(RequestForwardingAdmission.Accepted));
		call.ReleaseRequestWrites();
		await completion.WaitAsync(TimeSpan.FromSeconds(5));

		var rejected = publisher.Messages.OfType<ClientMessage.NotHandled>().ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(rejected.Select(x => x.CorrelationId), Is.EquivalentTo(requests.Select(x => x.InternalCorrId)));
			Assert.That(rejected.Select(x => x.Reason), Is.All.EqualTo(
				ClientMessage.NotHandled.Types.NotHandledReason.NotReady));
		});
	}

	[Test]
	public async Task a_closed_response_stream_cancels_blocked_and_queued_requests()
	{
		var publisher = new CapturingPublisher();
		var call = new FakeCall { BlockRequestWrites = true };
		var service = CreateService(call, publisher);
		var completion = service.Start();
		await call.WaitForWrites(1);
		var requests = CreateWriteRequests().Take(2).ToArray();

		Assert.That(service.TryForward(requests[0]), Is.EqualTo(RequestForwardingAdmission.Accepted));
		await call.RequestWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.That(service.TryForward(requests[1]), Is.EqualTo(RequestForwardingAdmission.Accepted));

		try
		{
			call.Responses.Writer.TryComplete();
			await completion.WaitAsync(TimeSpan.FromSeconds(5));

			var rejected = publisher.Messages.OfType<ClientMessage.NotHandled>().ToArray();
			Assert.Multiple(() =>
			{
				Assert.That(rejected.Select(x => x.CorrelationId),
					Is.EquivalentTo(requests.Select(x => x.InternalCorrId)));
				Assert.That(rejected.Select(x => x.Reason), Is.All.EqualTo(
					ClientMessage.NotHandled.Types.NotHandledReason.NotReady));
				Assert.That(call.Writes.Count(x => x.PayloadCase == Proto.FollowerFrame.PayloadOneofCase.Request),
					Is.EqualTo(1));
			});
		}
		finally
		{
			call.ReleaseRequestWrites();
			service.Stop();
			await completion;
		}
	}

	[Test]
	public async Task a_sent_request_is_rejected_when_the_stream_closes_before_its_response()
	{
		var publisher = new CapturingPublisher();
		var call = new FakeCall();
		var service = CreateService(call, publisher);
		var completion = service.Start();
		await call.WaitForWrites(1);
		var request = CreateWriteRequests().First();

		Assert.That(service.TryForward(request), Is.EqualTo(RequestForwardingAdmission.Accepted));
		await call.WaitForWrites(2);
		call.Responses.Writer.TryComplete();
		await completion.WaitAsync(TimeSpan.FromSeconds(5));

		var rejected = publisher.Messages.OfType<ClientMessage.NotHandled>().Single();
		Assert.Multiple(() =>
		{
			Assert.That(rejected.CorrelationId, Is.EqualTo(request.InternalCorrId));
			Assert.That(rejected.Reason, Is.EqualTo(ClientMessage.NotHandled.Types.NotHandledReason.NotReady));
		});
	}

	[Test]
	public async Task a_response_cannot_be_followed_by_a_local_rejection_for_the_same_request()
	{
		var publisher = new CapturingPublisher();
		var call = new FakeCall { BlockRequestWrites = true };
		var service = CreateService(call, publisher);
		var completion = service.Start();
		await call.WaitForWrites(1);
		var request = CreateWriteRequests().OfType<ClientMessage.TransactionWrite>().Single();

		Assert.That(service.TryForward(request), Is.EqualTo(RequestForwardingAdmission.Accepted));
		await call.RequestWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await call.Responses.Writer.WriteAsync(ForwardingGrpcCodec.ToGrpc(
			new ClientMessage.TransactionWriteCompleted(
				request.InternalCorrId,
				request.TransactionId,
				OperationResult.Success,
				null)));
		await WaitUntil(() => publisher.Messages.Count == 1);
		call.Responses.Writer.TryComplete();
		await completion.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Multiple(() =>
		{
			Assert.That(publisher.Messages.OfType<ClientMessage.TransactionWriteCompleted>(), Has.Exactly(1).Items);
			Assert.That(publisher.Messages.OfType<ClientMessage.NotHandled>(), Is.Empty);
		});
	}

	[Test]
	public async Task a_response_rejected_by_the_generation_fence_completes_as_not_ready()
	{
		var publisher = new CapturingPublisher();
		var call = new FakeCall();
		var service = CreateService(call, publisher, tryPublishResponse: _ => false);
		var completion = service.Start();
		await call.WaitForWrites(1);
		var request = CreateWriteRequests().OfType<ClientMessage.TransactionWrite>().Single();

		Assert.That(service.TryForward(request), Is.EqualTo(RequestForwardingAdmission.Accepted));
		await call.WaitForWrites(2);
		await call.Responses.Writer.WriteAsync(ForwardingGrpcCodec.ToGrpc(
			new ClientMessage.TransactionWriteCompleted(
				request.InternalCorrId,
				request.TransactionId,
				OperationResult.Success,
				null)));
		await WaitUntil(() => publisher.Messages.Count == 1);
		call.Responses.Writer.TryComplete();
		await completion.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Multiple(() =>
		{
			Assert.That(publisher.Messages.OfType<ClientMessage.TransactionWriteCompleted>(), Is.Empty);
			Assert.That(publisher.Messages.OfType<ClientMessage.NotHandled>().Single().CorrelationId,
				Is.EqualTo(request.InternalCorrId));
		});
	}

	[Test]
	public async Task stop_is_safe_after_the_stream_has_already_completed()
	{
		var call = new FakeCall();
		var service = CreateService(call);
		var completion = service.Start();
		await call.WaitForWrites(1);

		call.Responses.Writer.TryComplete();
		await completion.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.That(service.Stop, Throws.Nothing);
	}

	[Test]
	public async Task stop_completes_accepted_requests_that_have_not_been_sent()
	{
		var publisher = new CapturingPublisher();
		var call = new FakeCall { BlockRequestWrites = true };
		var service = CreateService(call, publisher);
		var completion = service.Start();
		await call.WaitForWrites(1);
		var requests = CreateWriteRequests().Take(2).ToArray();

		Assert.That(service.TryForward(requests[0]), Is.EqualTo(RequestForwardingAdmission.Accepted));
		await call.RequestWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.That(service.TryForward(requests[1]), Is.EqualTo(RequestForwardingAdmission.Accepted));

		service.Stop();
		await completion.WaitAsync(TimeSpan.FromSeconds(5));

		var rejected = publisher.Messages.OfType<ClientMessage.NotHandled>().ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(rejected.Select(x => x.CorrelationId), Is.EquivalentTo(requests.Select(x => x.InternalCorrId)));
			Assert.That(rejected.Select(x => x.Reason), Is.All.EqualTo(
				ClientMessage.NotHandled.Types.NotHandledReason.NotReady));
		});
	}

	private static GrpcRequestForwardingService CreateService(
		FakeCall call,
		CapturingPublisher publisher = null,
		FakeClient client = null,
		int requestQueueCapacity = 4,
		TryPublishForwardingResponse tryPublishResponse = null)
	{
		publisher ??= new CapturingPublisher();
		tryPublishResponse ??= message =>
		{
			publisher.Publish(message);
			return true;
		};
		return new GrpcRequestForwardingService(
			tryPublishResponse,
			publisher.Publish,
			client ?? new FakeClient(call),
			Guid.NewGuid(),
			new DnsEndPoint("leader.internal", 2113),
			requestQueueCapacity,
			new ForwardingSessionGeneration(1));
	}

	private static IReadOnlyList<ClientMessage.WriteRequestMessage> CreateWriteRequests()
	{
		var user = new ClaimsPrincipal();
		var @event = new Event(Guid.NewGuid(), "type", true, Array.Empty<byte>(), Array.Empty<byte>());
		return new ClientMessage.WriteRequestMessage[]
		{
			new ClientMessage.WriteEvents(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				"stream", ExpectedVersion.Any, @event, user),
			new ClientMessage.TransactionStart(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				"stream", ExpectedVersion.Any, user),
			new ClientMessage.TransactionWrite(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				1, new[] { @event }, user),
			new ClientMessage.TransactionCommit(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false, 1, user),
			new ClientMessage.DeleteStream(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				"stream", ExpectedVersion.Any, false, user)
		};
	}

	private static async Task WaitUntil(Func<bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (!condition())
		{
			await Task.Delay(10, timeout.Token);
		}
	}

	private sealed class FakeClient(FakeCall call) : IRequestForwardingGrpcClient
	{
		public int ForwardCalls { get; private set; }

		public IRequestForwardingGrpcCall Forward(CancellationToken cancellationToken)
		{
			ForwardCalls++;
			call.CancellationToken = cancellationToken;
			return call;
		}

		public void Dispose()
		{
		}
	}

	private sealed class FakeCall : IRequestForwardingGrpcCall
	{
		private readonly object _sync = new();
		private readonly TaskCompletionSource _requestWriteRelease =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly SemaphoreSlim _writeCountChanged = new(0);

		public Channel<Proto.LeaderFrame> Responses { get; } = Channel.CreateUnbounded<Proto.LeaderFrame>();
		public List<Proto.FollowerFrame> Writes { get; } = new();
		public TaskCompletionSource RequestWriteStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public CancellationToken CancellationToken { get; set; }
		public bool BlockRequestWrites { get; init; }
		public Exception RequestWriteException { get; init; }

		public async Task WriteAsync(Proto.FollowerFrame frame)
		{
			lock (_sync)
			{
				Writes.Add(frame);
			}
			_writeCountChanged.Release();

			if (frame.PayloadCase != Proto.FollowerFrame.PayloadOneofCase.Request)
			{
				return;
			}

			RequestWriteStarted.TrySetResult();
			if (BlockRequestWrites)
			{
				await _requestWriteRelease.Task.WaitAsync(CancellationToken);
			}

			if (RequestWriteException is not null)
			{
				throw RequestWriteException;
			}
		}

		public Task CompleteRequestAsync()
		{
			Responses.Writer.TryComplete();
			return Task.CompletedTask;
		}

		public async IAsyncEnumerable<Proto.LeaderFrame> ReadAllAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				CancellationToken);
			await foreach (var response in Responses.Reader.ReadAllAsync(linked.Token))
			{
				yield return response;
			}
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

		public void ReleaseRequestWrites() => _requestWriteRelease.TrySetResult();

		public void Dispose()
		{
			Responses.Writer.TryComplete();
		}
	}

	private sealed class CapturingPublisher : IPublisher
	{
		private readonly object _sync = new();
		private readonly List<Message> _messages = new();

		public IReadOnlyList<Message> Messages
		{
			get
			{
				lock (_sync)
				{
					return _messages.ToArray();
				}
			}
		}

		public void Publish(Message message)
		{
			lock (_sync)
			{
				_messages.Add(message);
			}
		}
	}
}

[TestFixture]
public class GrpcRequestForwardingSupervisorTests
{
	[Test]
	public void pre_replica_connects_to_the_leader_http_endpoint()
	{
		var fixture = CreateFixture();

		fixture.Supervisor.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));

		Assert.That(fixture.Factory.EndPoints.Single(), Is.EqualTo(fixture.Leader.HttpEndPoint));
	}

	[Test]
	public void all_supported_writes_are_admitted_without_rewriting_the_request()
	{
		var fixture = CreateFixture();
		fixture.Supervisor.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var requests = CreateWriteRequests();

		foreach (var request in requests)
		{
			fixture.Supervisor.Handle(new ClientMessage.ForwardMessage(request));
		}

		Assert.That(fixture.Factory.Services.Single().Requests, Is.EqualTo(requests).Using<Message>(ReferenceEquals));
	}

	[Test]
	public void a_forward_without_an_active_stream_is_ignored()
	{
		var fixture = CreateFixture();
		var message = new ClientMessage.ForwardMessage(CreateWriteRequests().First());

		Assert.That(() => fixture.Supervisor.Handle(message), Throws.Nothing);
		Assert.Multiple(() =>
		{
			Assert.That(fixture.Factory.Services, Is.Empty);
			Assert.That(fixture.Publisher.Messages.OfType<ClientMessage.NotHandled>(), Is.Empty);
		});
	}

	[Test]
	public void a_full_active_stream_completes_the_proxy_correlation_as_too_busy()
	{
		var fixture = CreateFixture();
		fixture.Supervisor.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		fixture.Factory.Services.Single().Admission = RequestForwardingAdmission.QueueFull;
		var request = CreateWriteRequests().First();

		fixture.Supervisor.Handle(new ClientMessage.ForwardMessage(request));

		var response = fixture.Publisher.Messages.OfType<ClientMessage.NotHandled>().Single();
		Assert.Multiple(() =>
		{
			Assert.That(response.CorrelationId, Is.EqualTo(request.InternalCorrId));
			Assert.That(response.Reason, Is.EqualTo(ClientMessage.NotHandled.Types.NotHandledReason.TooBusy));
		});
	}

	[Test]
	public void a_closed_active_stream_completes_the_proxy_correlation_as_not_ready()
	{
		var fixture = CreateFixture();
		fixture.Supervisor.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		fixture.Factory.Services.Single().Admission = RequestForwardingAdmission.Closed;
		var request = CreateWriteRequests().First();

		fixture.Supervisor.Handle(new ClientMessage.ForwardMessage(request));

		var response = fixture.Publisher.Messages.OfType<ClientMessage.NotHandled>().Single();
		Assert.Multiple(() =>
		{
			Assert.That(response.CorrelationId, Is.EqualTo(request.InternalCorrId));
			Assert.That(response.Reason, Is.EqualTo(ClientMessage.NotHandled.Types.NotHandledReason.NotReady));
		});
	}

	[Test]
	public void reconnect_keeps_a_healthy_stream_to_the_same_leader()
	{
		var fixture = CreateFixture();
		fixture.Supervisor.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var service = fixture.Factory.Services.Single();

		fixture.Supervisor.Handle(new ReplicationMessage.ReconnectToLeader(Guid.NewGuid(), fixture.Leader));

		Assert.Multiple(() =>
		{
			Assert.That(fixture.Factory.Services, Has.Exactly(1).Items);
			Assert.That(service.StopCalls, Is.Zero);
		});
	}

	[Test]
	public void reconnect_replaces_the_stream_when_the_http_endpoint_changes()
	{
		var fixture = CreateFixture();
		fixture.Supervisor.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var service = fixture.Factory.Services.Single();
		var movedLeader = CreateLeader(fixture.Leader.InstanceId, httpPort: 2213);

		fixture.Supervisor.Handle(new ReplicationMessage.ReconnectToLeader(Guid.NewGuid(), movedLeader));

		Assert.Multiple(() =>
		{
			Assert.That(fixture.Factory.Services, Has.Count.EqualTo(2));
			Assert.That(service.StopCalls, Is.EqualTo(1));
			Assert.That(fixture.Factory.SessionGenerations.Select(x => x.Value), Is.EqualTo(new long[] { 1, 2 }));
		});
	}

	[Test]
	public void a_replaced_stream_completes_local_failures_without_publishing_stale_leader_responses()
	{
		var fixture = CreateFixture();
		fixture.Supervisor.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var replaced = fixture.Factory.Services.Single();
		fixture.Supervisor.Handle(new ReplicationMessage.ReconnectToLeader(Guid.NewGuid(), CreateLeader()));
		var localFailureCorrelationId = Guid.NewGuid();

		replaced.Publish(new ClientMessage.TransactionWriteCompleted(
			Guid.NewGuid(), 42, OperationResult.Success, null));
		replaced.PublishLocalFailure(localFailureCorrelationId);

		Assert.Multiple(() =>
		{
			Assert.That(fixture.Publisher.Messages.OfType<ClientMessage.TransactionWriteCompleted>(), Is.Empty);
			Assert.That(fixture.Publisher.Messages.OfType<ClientMessage.NotHandled>().Single().CorrelationId,
				Is.EqualTo(localFailureCorrelationId));
		});
	}

	[Test]
	public async Task a_closed_stream_schedules_one_generation_fenced_reconnect()
	{
		var fixture = CreateFixture();
		fixture.Supervisor.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		fixture.Factory.Services.Single().Complete();
		await WaitUntil(() => fixture.Publisher.Messages.OfType<GrpcRequestForwardingMessage.StreamClosed>().Any());
		var closed = fixture.Publisher.Messages.OfType<GrpcRequestForwardingMessage.StreamClosed>().Single();

		fixture.Supervisor.Handle(closed);
		fixture.Supervisor.Handle(closed);
		var reconnect = fixture.Publisher.Messages.OfType<TimerMessage.Schedule>().Single();
		fixture.Supervisor.Handle((GrpcRequestForwardingMessage.Reconnect)reconnect.ReplyMessage);

		Assert.That(fixture.Factory.Services, Has.Count.EqualTo(2));
	}

	[Test]
	public async Task a_stale_stream_close_cannot_reconnect_after_leader_replacement()
	{
		var fixture = CreateFixture();
		fixture.Supervisor.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var stale = fixture.Factory.Services.Single();

		fixture.Supervisor.Handle(new ReplicationMessage.ReconnectToLeader(Guid.NewGuid(), CreateLeader()));
		stale.Complete();
		await WaitUntil(() => fixture.Publisher.Messages.OfType<GrpcRequestForwardingMessage.StreamClosed>().Any());
		var closed = fixture.Publisher.Messages.OfType<GrpcRequestForwardingMessage.StreamClosed>().First();
		fixture.Supervisor.Handle(closed);

		Assert.That(fixture.Publisher.Messages.OfType<TimerMessage.Schedule>(), Is.Empty);
	}

	[Test]
	public void leaving_replica_states_stops_the_stream_and_ignores_future_writes()
	{
		var fixture = CreateFixture();
		fixture.Supervisor.Handle(new SystemMessage.BecomePreReplica(
			Guid.NewGuid(), Guid.NewGuid(), fixture.Leader));
		var service = fixture.Factory.Services.Single();

		fixture.Supervisor.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
		fixture.Supervisor.Handle(new ClientMessage.ForwardMessage(CreateWriteRequests().First()));

		Assert.Multiple(() =>
		{
			Assert.That(service.StopCalls, Is.EqualTo(1));
			Assert.That(service.Requests, Is.Empty);
		});
	}

	private static Fixture CreateFixture()
	{
		var publisher = new CapturingPublisher();
		var factory = new FakeServiceFactory();
		return new Fixture(
			new GrpcRequestForwardingSupervisor(
				publisher,
				factory,
				_ => { },
				TimeSpan.FromMilliseconds(100)),
			factory,
			publisher,
			CreateLeader());
	}

	private static IReadOnlyList<ClientMessage.WriteRequestMessage> CreateWriteRequests()
	{
		var user = new ClaimsPrincipal();
		var @event = new Event(Guid.NewGuid(), "type", true, Array.Empty<byte>(), Array.Empty<byte>());
		return new ClientMessage.WriteRequestMessage[]
		{
			new ClientMessage.WriteEvents(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				"stream", ExpectedVersion.Any, @event, user),
			new ClientMessage.TransactionStart(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				"stream", ExpectedVersion.Any, user),
			new ClientMessage.TransactionWrite(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				1, new[] { @event }, user),
			new ClientMessage.TransactionCommit(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false, 1, user),
			new ClientMessage.DeleteStream(Guid.NewGuid(), Guid.NewGuid(), IEnvelope.NoOp, false,
				"stream", ExpectedVersion.Any, false, user)
		};
	}

	private static MemberInfo CreateLeader(Guid? instanceId = null, int httpPort = 2113) => MemberInfo.ForVNode(
		instanceId ?? Guid.NewGuid(),
		DateTime.UtcNow,
		VNodeState.Leader,
		true,
		new DnsEndPoint("leader-replication.internal", 1112),
		new DnsEndPoint("leader-replication.internal", 1113),
		null,
		null,
		new DnsEndPoint("leader.internal", httpPort),
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

	private static async Task WaitUntil(Func<bool> condition)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (!condition())
		{
			await Task.Delay(10, timeout.Token);
		}
	}

	private sealed record Fixture(
		GrpcRequestForwardingSupervisor Supervisor,
		FakeServiceFactory Factory,
		CapturingPublisher Publisher,
		MemberInfo Leader);

	private sealed class FakeServiceFactory : IGrpcRequestForwardingServiceFactory
	{
		public List<EndPoint> EndPoints { get; } = new();
		public List<ForwardingSessionGeneration> SessionGenerations { get; } = new();
		public List<FakeService> Services { get; } = new();

		public IGrpcRequestForwardingService Create(
			TryPublishForwardingResponse tryPublishResponse,
			Action<ClientMessage.NotHandled> publishLocalFailure,
			EndPoint leaderEndPoint,
			ForwardingSessionGeneration sessionGeneration)
		{
			EndPoints.Add(leaderEndPoint);
			SessionGenerations.Add(sessionGeneration);
			var service = new FakeService(tryPublishResponse, publishLocalFailure);
			Services.Add(service);
			return service;
		}
	}

	private sealed class FakeService(
		TryPublishForwardingResponse tryPublishResponse,
		Action<ClientMessage.NotHandled> publishLocalFailure) : IGrpcRequestForwardingService
	{
		private readonly TaskCompletionSource _completion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task Task => _completion.Task;
		public List<ClientMessage.WriteRequestMessage> Requests { get; } = new();
		public int StopCalls { get; private set; }
		public RequestForwardingAdmission Admission { get; set; } = RequestForwardingAdmission.Accepted;

		public Task Start() => Task;

		public RequestForwardingAdmission TryForward(ClientMessage.WriteRequestMessage message)
		{
			Requests.Add(message);
			return Admission;
		}

		public void Stop()
		{
			StopCalls++;
			_completion.TrySetResult();
		}

		public void Complete() => _completion.TrySetResult();

		public void Publish(Message message) => tryPublishResponse(message);

		public void PublishLocalFailure(Guid correlationId) => publishLocalFailure(new ClientMessage.NotHandled(
			correlationId,
			ClientMessage.NotHandled.Types.NotHandledReason.NotReady,
			"Request forwarding ended before the request completed."));
	}

	private sealed class CapturingPublisher : IPublisher
	{
		private readonly object _sync = new();
		private readonly List<Message> _messages = new();

		public IReadOnlyList<Message> Messages
		{
			get
			{
				lock (_sync)
				{
					return _messages.ToArray();
				}
			}
		}

		public void Publish(Message message)
		{
			lock (_sync)
			{
				_messages.Add(message);
			}
		}
	}
}
