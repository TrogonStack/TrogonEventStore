using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Cluster.Settings;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Replication;
using EventStore.Core.Services.Transport.Grpc.Replication;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Plugins.Transforms;
using NUnit.Framework;
using Proto = EventStore.Replication;
using RpcException = Grpc.Core.RpcException;
using Status = Grpc.Core.Status;
using StatusCode = Grpc.Core.StatusCode;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Replication;

[TestFixture]
public class GrpcReplicaServiceTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	[TestCase(ReplicaPromotability.Promotable, true)]
	[TestCase(ReplicaPromotability.NonPromotable, false)]
	public async Task sends_complete_subscription_handshake(
		ReplicaPromotability promotability,
		bool expectedPromotability)
	{
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		var replicaInstanceId = Guid.NewGuid();
		var chunkId = Guid.NewGuid();
		var epoch = new EpochRecord(700, 7, Guid.NewGuid(), 600, DateTime.UtcNow, Guid.NewGuid());
		var dataSource = new FakeSubscriptionDataSource(800, chunkId, new[] { epoch });
		var fixture = CreateFixture(dataSource, replicaInstanceId, promotability);
		_ = fixture.Service.Start();

		await fixture.Service.HandleAsync(
			new ReplicationMessage.SubscribeToLeader(Guid.NewGuid(), leaderId, subscriptionId),
			CancellationToken.None);
		var frame = await fixture.Call.ReadRequestAsync();
		var subscribe = frame.Subscribe;

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.ReplicaFrame.PayloadOneofCase.Subscribe));
			Assert.That(subscribe.Version, Is.EqualTo(ReplicationSubscriptionVersions.V_CURRENT));
			Assert.That(subscribe.LogPosition, Is.EqualTo(800));
			Assert.That(ToGuid(subscribe.ChunkId), Is.EqualTo(chunkId));
			Assert.That(ToGuid(subscribe.ReplicaInstanceId), Is.EqualTo(replicaInstanceId));
			Assert.That(ToGuid(subscribe.LeaderId), Is.EqualTo(leaderId));
			Assert.That(ToGuid(subscribe.SubscriptionId), Is.EqualTo(subscriptionId));
			Assert.That(subscribe.AdvertisedEndpoint.Address, Is.EqualTo("replica.internal"));
			Assert.That(subscribe.AdvertisedEndpoint.Port, Is.EqualTo(2113));
			Assert.That(subscribe.IsPromotable, Is.EqualTo(expectedPromotability));
			Assert.That(subscribe.LastEpochs.Single().EpochPosition, Is.EqualTo(epoch.EpochPosition));
			Assert.That(subscribe.LastEpochs.Single().EpochNumber, Is.EqualTo(epoch.EpochNumber));
			Assert.That(ToGuid(subscribe.LastEpochs.Single().EpochId), Is.EqualTo(epoch.EpochId));
			Assert.That(dataSource.RequestedChunkPosition, Is.EqualTo(800));
			Assert.That(dataSource.RequestedEpochCount, Is.EqualTo(ClusterConsts.SubscriptionLastEpochCount));
		});

		Assert.That(
			fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionEstablished>(),
			Is.Empty);
		fixture.Call.WriteResponse(ReplicationGrpcCodec.ToGrpc(new ReplicationTrackingMessage.ReplicatedTo(800)));
		AssertEx.IsOrBecomesTrue(() =>
			fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionEstablished>().Count() == 1);
		var established = fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionEstablished>().Single();
		Assert.Multiple(() =>
		{
			Assert.That(established.ConnectionId, Is.EqualTo(fixture.Service.ConnectionId));
			Assert.That(established.VNodeEndPoint, Is.EqualTo(fixture.LeaderEndPoint));
		});

		await fixture.Service.StopAsync();
	}

	[Test]
	public async Task preserves_order_while_coalescing_pending_acknowledgements()
	{
		var subscriptionId = Guid.NewGuid();
		var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var fixture = CreateFixture(writeGate: writeGate);
		_ = fixture.Service.Start();
		await fixture.Service.HandleAsync(
			new ReplicationMessage.SubscribeToLeader(Guid.NewGuid(), Guid.NewGuid(), subscriptionId),
			CancellationToken.None);
		await fixture.Call.WriteStarted.Task.WaitAsync(Timeout);

		fixture.Service.Handle(new ReplicationMessage.AckLogPosition(subscriptionId, 100, 90));
		fixture.Service.Handle(new ReplicationMessage.AckLogPosition(subscriptionId, 200, 180));
		writeGate.SetResult();

		var subscribe = await fixture.Call.ReadRequestAsync();
		var coalescedAck = await fixture.Call.ReadRequestAsync();
		fixture.Service.Handle(new ReplicationMessage.AckLogPosition(subscriptionId, 300, 270));
		var nextAck = await fixture.Call.ReadRequestAsync();

		Assert.Multiple(() =>
		{
			Assert.That(subscribe.PayloadCase, Is.EqualTo(Proto.ReplicaFrame.PayloadOneofCase.Subscribe));
			Assert.That(coalescedAck.Acknowledgement.ReplicationLogPosition, Is.EqualTo(200));
			Assert.That(coalescedAck.Acknowledgement.WriterLogPosition, Is.EqualTo(180));
			Assert.That(nextAck.Acknowledgement.ReplicationLogPosition, Is.EqualTo(300));
			Assert.That(nextAck.Acknowledgement.WriterLogPosition, Is.EqualTo(270));
		});

		await fixture.Service.StopAsync();
	}

	[Test]
	public async Task publishes_every_leader_response()
	{
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		var fixture = CreateFixture();
		_ = fixture.Service.Start();

		var chunkHeader = new ChunkHeader(
			TFChunk.CurrentChunkVersion,
			TFChunk.CurrentChunkVersion,
			4096,
			1,
			1,
			false,
			Guid.NewGuid(),
			TransformType.Identity);
		fixture.Call.WriteResponse(ReplicationGrpcCodec.ToGrpc(
			new ReplicationMessage.ReplicaSubscriptionRetry(leaderId, subscriptionId)));
		fixture.Call.WriteResponse(ReplicationGrpcCodec.ToGrpc(
			new ReplicationMessage.ReplicaSubscribed(leaderId, subscriptionId, 100)));
		fixture.Call.WriteResponse(ReplicationGrpcCodec.ToGrpc(
			new ReplicationMessage.CreateChunk(
				leaderId, subscriptionId, chunkHeader, 8192, false, ReadOnlyMemory<byte>.Empty)));
		fixture.Call.WriteResponse(ReplicationGrpcCodec.ToGrpc(
			new ReplicationMessage.RawChunkBulk(
				leaderId, subscriptionId, 1, 1, 128, new byte[] { 1 }, false)));
		fixture.Call.WriteResponse(ReplicationGrpcCodec.ToGrpc(
			new ReplicationMessage.DataChunkBulk(
				leaderId, subscriptionId, 1, 1, 256, new byte[] { 2 }, false)));
		fixture.Call.WriteResponse(ReplicationGrpcCodec.ToGrpc(
			new ReplicationMessage.FollowerAssignment(leaderId, subscriptionId)));
		fixture.Call.WriteResponse(ReplicationGrpcCodec.ToGrpc(
			new ReplicationMessage.CloneAssignment(leaderId, subscriptionId)));
		fixture.Call.WriteResponse(ReplicationGrpcCodec.ToGrpc(
			new ReplicationMessage.DropSubscription(leaderId, subscriptionId)));
		fixture.Call.WriteResponse(ReplicationGrpcCodec.ToGrpc(
			new ReplicationTrackingMessage.ReplicatedTo(512)));
		fixture.Call.CompleteResponses();

		await fixture.Service.Completion.WaitAsync(Timeout);

		var published = fixture.Publisher.Messages
			.Where(message => message is not SystemMessage.VNodeConnectionEstablished and not SystemMessage.VNodeConnectionLost)
			.ToArray();
		Assert.That(published.Select(message => message.GetType()), Is.EqualTo(new[]
		{
			typeof(ReplicationMessage.ReplicaSubscriptionRetry),
			typeof(ReplicationMessage.ReplicaSubscribed),
			typeof(ReplicationMessage.CreateChunk),
			typeof(ReplicationMessage.RawChunkBulk),
			typeof(ReplicationMessage.DataChunkBulk),
			typeof(ReplicationMessage.FollowerAssignment),
			typeof(ReplicationMessage.CloneAssignment),
			typeof(ReplicationMessage.DropSubscription),
			typeof(ReplicationTrackingMessage.LeaderReplicatedTo)
		}));
		Assert.That(
			((ReplicationMessage.ReplicaSubscribed)published[1]).LeaderEndPoint,
			Is.EqualTo(fixture.LeaderEndPoint));
		Assert.That(
			fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionEstablished>(),
			Has.Exactly(1).Items);
	}

	[Test]
	public async Task normal_remote_completion_publishes_connection_loss_once()
	{
		var fixture = CreateFixture();
		_ = fixture.Service.Start();
		fixture.Call.CompleteResponses();

		await fixture.Service.Completion.WaitAsync(Timeout);

		AssertConnectionLifecycle(fixture, expectedLossCount: 1);
		Assert.Multiple(() =>
		{
			Assert.That(fixture.Call.RequestCompleted, Is.True);
			Assert.That(fixture.Call.Disposed, Is.True);
		});
	}

	[TestCase(StatusCode.PermissionDenied)]
	[TestCase(StatusCode.Unavailable)]
	public async Task stream_failure_before_first_response_does_not_publish_connection_established(
		StatusCode statusCode)
	{
		var fixture = CreateFixture();
		_ = fixture.Service.Start();
		fixture.Call.CompleteResponses(new RpcException(new Status(statusCode, statusCode.ToString())));

		await fixture.Service.Completion.WaitAsync(Timeout);

		Assert.Multiple(() =>
		{
			Assert.That(
				fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionEstablished>(),
				Is.Empty);
			Assert.That(
				fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionLost>(),
				Has.Exactly(1).Items);
		});
	}

	[Test]
	public async Task malformed_first_response_does_not_publish_connection_established()
	{
		var fixture = CreateFixture();
		_ = fixture.Service.Start();
		fixture.Call.WriteResponse(new Proto.LeaderFrame());
		fixture.Call.CompleteResponses();

		await fixture.Service.Completion.WaitAsync(Timeout);

		Assert.Multiple(() =>
		{
			Assert.That(
				fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionEstablished>(),
				Is.Empty);
			Assert.That(
				fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionLost>(),
				Has.Exactly(1).Items);
		});
	}

	[Test]
	public async Task stream_failure_publishes_connection_loss_once()
	{
		var fixture = CreateFixture();
		_ = fixture.Service.Start();
		fixture.Call.CompleteResponses(new RpcException(new Status(StatusCode.Unavailable, "unavailable")));

		await fixture.Service.Completion.WaitAsync(Timeout);

		AssertConnectionLifecycle(fixture, expectedLossCount: 1);
		Assert.Multiple(() =>
		{
			Assert.That(fixture.Call.CancellationRequested, Is.True);
			Assert.That(fixture.Call.Disposed, Is.True);
		});
	}

	[Test]
	public async Task expected_cancellation_does_not_publish_connection_loss()
	{
		var fixture = CreateFixture();
		_ = fixture.Service.Start();

		await fixture.Service.StopAsync().AsTask().WaitAsync(Timeout);

		AssertConnectionLifecycle(fixture, expectedLossCount: 0);
		Assert.Multiple(() =>
		{
			Assert.That(fixture.Call.RequestCompleted, Is.False);
			Assert.That(fixture.Call.Disposed, Is.True);
			Assert.That(fixture.Service.Completion.IsCompletedSuccessfully, Is.True);
		});
	}

	[Test]
	public async Task stopping_the_service_disposes_its_client()
	{
		var fixture = CreateFixture();
		_ = fixture.Service.Start();

		await fixture.Service.StopAsync().AsTask().WaitAsync(Timeout);

		Assert.That(fixture.Client.Disposed, Is.True);
	}

	[Test]
	public async Task expected_cancellation_does_not_start_a_graceful_request_half_close()
	{
		var completeRequestGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var fixture = CreateFixture(completeRequestGate: completeRequestGate);
		_ = fixture.Service.Start();

		var stop = fixture.Service.StopAsync().AsTask();
		try
		{
			await stop.WaitAsync(TimeSpan.FromMilliseconds(250));
		}
		finally
		{
			completeRequestGate.TrySetResult();
			await stop.WaitAsync(Timeout);
		}

		Assert.That(fixture.Call.CompleteRequestStarted.Task.IsCompleted, Is.False);
	}

	private static Fixture CreateFixture(
		FakeSubscriptionDataSource dataSource = null,
		Guid? replicaInstanceId = null,
		ReplicaPromotability promotability = ReplicaPromotability.Promotable,
		TaskCompletionSource writeGate = null,
		TaskCompletionSource completeRequestGate = null)
	{
		var call = new FakeReplicationGrpcCall(writeGate, completeRequestGate);
		var publisher = new ConcurrentPublisher();
		var leaderEndPoint = new DnsEndPoint("leader.internal", 2113);
		var client = new FakeReplicationGrpcClient(call);
		var service = new GrpcReplicaService(
			publisher,
			client,
			dataSource ?? new FakeSubscriptionDataSource(100, Guid.Empty, Array.Empty<EpochRecord>()),
			replicaInstanceId ?? Guid.NewGuid(),
			leaderEndPoint,
			new DnsEndPoint("replica.internal", 2113),
			promotability,
			2);

		return new Fixture(service, client, call, publisher, leaderEndPoint);
	}

	private static void AssertConnectionLifecycle(Fixture fixture, int expectedLossCount)
	{
		var established = fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionEstablished>().ToArray();
		var losses = fixture.Publisher.Messages.OfType<SystemMessage.VNodeConnectionLost>().ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(established, Is.Empty);
			Assert.That(losses, Has.Length.EqualTo(expectedLossCount));
			Assert.That(losses.All(loss => loss.ConnectionId == fixture.Service.ConnectionId), Is.True);
			Assert.That(losses.All(loss => Equals(loss.VNodeEndPoint, fixture.LeaderEndPoint)), Is.True);
		});
	}

	private static Guid ToGuid(EventStore.Client.UUID value) =>
		EventStore.Core.Services.Transport.Grpc.Uuid.FromDto(value).ToGuid();

	private sealed record Fixture(
		GrpcReplicaService Service,
		FakeReplicationGrpcClient Client,
		FakeReplicationGrpcCall Call,
		ConcurrentPublisher Publisher,
		EndPoint LeaderEndPoint);

	private sealed class ConcurrentPublisher : IPublisher
	{
		public ConcurrentQueue<Message> Messages { get; } = new();

		public void Publish(Message message) => Messages.Enqueue(message);
	}

	private sealed class FakeReplicationGrpcClient(FakeReplicationGrpcCall call) : IReplicationGrpcClient
	{
		public bool Disposed { get; private set; }

		public IReplicationGrpcCall Replicate(CancellationToken cancellationToken)
		{
			call.SetCancellationToken(cancellationToken);
			return call;
		}

		public void Dispose() => Disposed = true;
	}

	private sealed class FakeReplicationGrpcCall(
		TaskCompletionSource writeGate = null,
		TaskCompletionSource completeRequestGate = null) : IReplicationGrpcCall
	{
		private readonly Channel<Proto.ReplicaFrame> _requests = Channel.CreateUnbounded<Proto.ReplicaFrame>();
		private readonly Channel<Proto.LeaderFrame> _responses = Channel.CreateUnbounded<Proto.LeaderFrame>();
		private readonly Task _writeGate = writeGate?.Task ?? Task.CompletedTask;
		private CancellationToken _cancellationToken;

		public TaskCompletionSource WriteStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource CompleteRequestStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public bool RequestCompleted { get; private set; }
		public bool CancellationRequested => _cancellationToken.IsCancellationRequested;
		public bool Disposed { get; private set; }

		public void SetCancellationToken(CancellationToken cancellationToken) =>
			_cancellationToken = cancellationToken;

		public async Task WriteAsync(Proto.ReplicaFrame frame)
		{
			WriteStarted.TrySetResult();
			await _writeGate.WaitAsync(_cancellationToken);
			await _requests.Writer.WriteAsync(frame, _cancellationToken);
		}

		public async Task CompleteRequestAsync()
		{
			CompleteRequestStarted.TrySetResult();
			if (completeRequestGate is not null)
			{
				await completeRequestGate.Task;
			}

			RequestCompleted = true;
			_requests.Writer.TryComplete();
		}

		public async IAsyncEnumerable<Proto.LeaderFrame> ReadAllAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			await foreach (var frame in _responses.Reader.ReadAllAsync(cancellationToken))
			{
				yield return frame;
			}
		}

		public async Task<Proto.ReplicaFrame> ReadRequestAsync() =>
			await _requests.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

		public void WriteResponse(Proto.LeaderFrame frame) =>
			_responses.Writer.TryWrite(frame);

		public void CompleteResponses(Exception exception = null) =>
			_responses.Writer.TryComplete(exception);

		public void Dispose() => Disposed = true;
	}

	private sealed class FakeSubscriptionDataSource(
		long logPosition,
		Guid chunkId,
		IReadOnlyList<EpochRecord> epochs) : IReplicaSubscriptionDataSource
	{
		public int RequestedEpochCount { get; private set; }
		public long RequestedChunkPosition { get; private set; }

		public long ReadNonFlushed() => logPosition;

		public ValueTask<Guid> GetCurrentChunkIdAsync(long position, CancellationToken cancellationToken)
		{
			RequestedChunkPosition = position;
			return ValueTask.FromResult(chunkId);
		}

		public ValueTask<IReadOnlyList<EpochRecord>> GetLastEpochsAsync(
			int maxCount,
			CancellationToken cancellationToken)
		{
			RequestedEpochCount = maxCount;
			return ValueTask.FromResult(epochs);
		}
	}
}
