using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Replication;
using EventStore.Core.Services.Transport.Grpc.Replication;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Plugins.Transforms;
using Grpc.Core;
using NUnit.Framework;
using Proto = EventStore.Replication;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Replication;

[TestFixture]
public class GrpcReplicationSessionTests
{
	[Test]
	public async Task owned_bulk_frames_retain_the_message_payload_arrays()
	{
		var writer = new ObservingStreamWriter(2);
		await using var session = new GrpcReplicationSession(
			ReplicationSessionIdentity.ForInsecureSystem(Guid.NewGuid()),
			Guid.NewGuid(), writer, 8, CancellationToken.None);
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		var rawBytes = new byte[] { 1, 2, 3 };
		var dataBytes = new byte[] { 4, 5, 6 };
		session.TrySend(new ReplicationMessage.RawChunkBulk(
			leaderId, subscriptionId, 1, 2, 10, rawBytes, false));
		session.TrySend(new ReplicationMessage.DataChunkBulk(
			leaderId, subscriptionId, 1, 2, 20, dataBytes, false));

		await writer.Written.WaitAsync(TimeSpan.FromSeconds(5));
		session.Close("test complete");
		await session.Completion;
		var frames = writer.Messages.ToArray();

		Assert.Multiple(() =>
		{
			Assert.That(MemoryMarshal.TryGetArray(
				frames[0].RawChunkBulk.RawBytes.Memory, out ArraySegment<byte> rawSegment), Is.True);
			Assert.That(rawSegment.Array, Is.SameAs(rawBytes));
			Assert.That(MemoryMarshal.TryGetArray(
				frames[1].DataChunkBulk.DataBytes.Memory, out ArraySegment<byte> dataSegment), Is.True);
			Assert.That(dataSegment.Array, Is.SameAs(dataBytes));
		});
	}

	[Test]
	public async Task leader_messages_are_written_in_order_by_one_writer()
	{
		var writer = new ObservingStreamWriter();
		await using var session = new GrpcReplicationSession(
			ReplicationSessionIdentity.ForInsecureSystem(Guid.NewGuid()),
			Guid.NewGuid(), writer, 8, CancellationToken.None);
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		session.TrySend(new ReplicationMessage.ReplicaSubscribed(leaderId, subscriptionId, 10));
		session.TrySend(new ReplicationMessage.FollowerAssignment(leaderId, subscriptionId));
		session.TrySend(new ReplicationTrackingMessage.ReplicatedTo(20));

		await writer.Written.WaitAsync(TimeSpan.FromSeconds(5));
		session.Close("test complete");
		await session.Completion;

		Assert.Multiple(() =>
		{
			Assert.That(writer.Messages.Select(x => x.PayloadCase), Is.EqualTo(new[]
			{
				Proto.LeaderFrame.PayloadOneofCase.Subscribed,
				Proto.LeaderFrame.PayloadOneofCase.FollowerAssignment,
				Proto.LeaderFrame.PayloadOneofCase.ReplicatedTo
			}));
			Assert.That(writer.MaxConcurrentWrites, Is.EqualTo(1));
			Assert.That(session.GetStatistics().TotalBytesSent, Is.GreaterThan(0));
			Assert.That(session.GetStatistics().PendingSendBytes, Is.Zero);
		});
	}

	[Test]
	public async Task sent_position_advances_only_after_the_response_write_completes()
	{
		var writer = new BlockingStreamWriter(1);
		await using var session = new GrpcReplicationSession(
			ReplicationSessionIdentity.ForInsecureSystem(Guid.NewGuid()),
			Guid.NewGuid(), writer, 8, CancellationToken.None);
		session.TrySend(new ReplicationMessage.ReplicaSubscribed(Guid.NewGuid(), Guid.NewGuid(), 100));

		await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.That(session.SentReplicationPosition, Is.EqualTo(-1));

		writer.Release.TrySetResult();
		await writer.Written.WaitAsync(TimeSpan.FromSeconds(5));
		AssertEx.IsOrBecomesTrue(() => session.SentReplicationPosition == 100);
	}

	[TestCase(false, 2050)]
	[TestCase(true, 3000)]
	public async Task raw_chunk_sent_position_preserves_transport_progress(
		bool completeChunk,
		long expectedPosition)
	{
		var writer = new ObservingStreamWriter(2);
		await using var session = new GrpcReplicationSession(
			ReplicationSessionIdentity.ForInsecureSystem(Guid.NewGuid()),
			Guid.NewGuid(), writer, 8, CancellationToken.None);
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		var chunkHeader = ChunkHeaderForPositionTests(isScavenged: true);
		session.TrySend(new ReplicationMessage.CreateChunk(
			leaderId, subscriptionId, chunkHeader, 4096, true, ReadOnlyMemory<byte>.Empty));
		session.TrySend(new ReplicationMessage.RawChunkBulk(
			leaderId,
			subscriptionId,
			chunkHeader.ChunkStartNumber,
			chunkHeader.ChunkEndNumber,
			ChunkHeader.Size,
			new byte[50],
			completeChunk));

		await writer.Written.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.That(session.SentReplicationPosition, Is.EqualTo(expectedPosition));
	}

	[TestCase(false, 2050)]
	[TestCase(true, 3000)]
	public async Task data_chunk_sent_position_preserves_log_progress(
		bool completeChunk,
		long expectedPosition)
	{
		var writer = new ObservingStreamWriter(2);
		await using var session = new GrpcReplicationSession(
			ReplicationSessionIdentity.ForInsecureSystem(Guid.NewGuid()),
			Guid.NewGuid(), writer, 8, CancellationToken.None);
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		var chunkHeader = ChunkHeaderForPositionTests(isScavenged: false);
		session.TrySend(new ReplicationMessage.CreateChunk(
			leaderId, subscriptionId, chunkHeader, 4096, false, ReadOnlyMemory<byte>.Empty));
		session.TrySend(new ReplicationMessage.DataChunkBulk(
			leaderId,
			subscriptionId,
			chunkHeader.ChunkStartNumber,
			chunkHeader.ChunkEndNumber,
			chunkHeader.ChunkStartPosition,
			new byte[50],
			completeChunk));

		await writer.Written.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.That(session.SentReplicationPosition, Is.EqualTo(expectedPosition));
	}

	[Test]
	public async Task completing_mid_chunk_data_sent_position_reaches_chunk_end_without_create_chunk()
	{
		var writer = new ObservingStreamWriter(2);
		await using var session = new GrpcReplicationSession(
			ReplicationSessionIdentity.ForInsecureSystem(Guid.NewGuid()),
			Guid.NewGuid(), writer, 8, CancellationToken.None);
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		var chunkHeader = ChunkHeaderForPositionTests(isScavenged: false);
		session.TrySend(new ReplicationMessage.ReplicaSubscribed(
			leaderId, subscriptionId, chunkHeader.ChunkStartPosition + 50));
		session.TrySend(new ReplicationMessage.DataChunkBulk(
			leaderId,
			subscriptionId,
			chunkHeader.ChunkStartNumber,
			chunkHeader.ChunkEndNumber,
			chunkHeader.ChunkStartPosition + 50,
			new byte[50],
			completeChunk: true,
			chunkEndPosition: chunkHeader.ChunkEndPosition));

		await writer.Written.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.That(session.SentReplicationPosition, Is.EqualTo(chunkHeader.ChunkEndPosition));
	}

	[Test]
	public async Task owned_bulk_payloads_remain_available_while_writes_are_blocked()
	{
		var writer = new BlockingStreamWriter(2);
		await using var session = new GrpcReplicationSession(
			ReplicationSessionIdentity.ForInsecureSystem(Guid.NewGuid()),
			Guid.NewGuid(), writer, 8, CancellationToken.None);
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		session.TrySend(new ReplicationMessage.RawChunkBulk(
			leaderId, subscriptionId, 1, 2, 10, new byte[] { 1, 2, 3 }, false));

		await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		session.TrySend(new ReplicationMessage.DataChunkBulk(
			leaderId, subscriptionId, 1, 2, 20, new byte[] { 4, 5, 6 }, false));
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		writer.Release.TrySetResult();
		await writer.Written.WaitAsync(TimeSpan.FromSeconds(5));
		session.Close("test complete");
		await session.Completion;
		var frames = writer.Messages.ToArray();

		Assert.Multiple(() =>
		{
			Assert.That(frames[0].RawChunkBulk.RawBytes.ToByteArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
			Assert.That(frames[1].DataChunkBulk.DataBytes.ToByteArray(), Is.EqualTo(new byte[] { 4, 5, 6 }));
		});
	}

	[Test]
	public async Task a_full_queue_does_not_block_the_sender()
	{
		var writer = new BlockingStreamWriter(3);
		await using var session = new GrpcReplicationSession(
			ReplicationSessionIdentity.ForInsecureSystem(Guid.NewGuid()),
			Guid.NewGuid(), writer, 1, CancellationToken.None);
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		session.TrySend(new ReplicationMessage.ReplicaSubscribed(leaderId, subscriptionId, 10));
		await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		session.TrySend(new ReplicationMessage.FollowerAssignment(leaderId, subscriptionId));
		var thirdMessage = new ReplicationMessage.CloneAssignment(leaderId, subscriptionId);
		var send = Task.Run(() => session.TrySend(thirdMessage));
		var sendCompletedWithoutWaitingForTheWriter = await Task.WhenAny(
			send, Task.Delay(TimeSpan.FromMilliseconds(250))) == send;
		var sendResult = await send;

		writer.Release.TrySetResult();
		AssertEx.IsOrBecomesTrue(() => session.SendQueueSize == 0);
		var retryResult = session.TrySend(thirdMessage);
		await writer.Written.WaitAsync(TimeSpan.FromSeconds(5));
		var closedBeforeCleanup = session.IsClosed;
		session.Close("test complete");
		await session.Completion;

		Assert.Multiple(() =>
		{
			Assert.That(sendCompletedWithoutWaitingForTheWriter, Is.True);
			Assert.That(sendResult, Is.EqualTo(ReplicationSendResult.QueueFull));
			Assert.That(retryResult, Is.EqualTo(ReplicationSendResult.Sent));
			Assert.That(closedBeforeCleanup, Is.False);
			Assert.That(writer.Messages, Has.Count.EqualTo(3));
			Assert.That(session.GetStatistics().PendingSendBytes, Is.Zero);
		});
	}

	[Test]
	public async Task concurrent_producers_receive_bounded_admission_without_closing_the_session()
	{
		var writer = new BlockingStreamWriter(3);
		await using var session = new GrpcReplicationSession(
			ReplicationSessionIdentity.ForInsecureSystem(Guid.NewGuid()),
			Guid.NewGuid(), writer, 1, CancellationToken.None);
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		session.TrySend(new ReplicationMessage.ReplicaSubscribed(leaderId, subscriptionId, 10));
		await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		using var start = new ManualResetEventSlim();
		using var entered = new CountdownEvent(2);
		var firstMessage = new ReplicationMessage.FollowerAssignment(leaderId, subscriptionId);
		var secondMessage = new ReplicationMessage.CloneAssignment(leaderId, subscriptionId);
		var first = Task.Run(() =>
		{
			start.Wait();
			entered.Signal();
			return (Message: (Message)firstMessage, Result: session.TrySend(firstMessage));
		});
		var second = Task.Run(() =>
		{
			start.Wait();
			entered.Signal();
			return (Message: (Message)secondMessage, Result: session.TrySend(secondMessage));
		});
		start.Set();
		Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
		var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

		writer.Release.TrySetResult();
		AssertEx.IsOrBecomesTrue(() => session.SendQueueSize == 0);
		var rejected = results.Single(x => x.Result == ReplicationSendResult.QueueFull);
		var retryResult = session.TrySend(rejected.Message);
		await writer.Written.WaitAsync(TimeSpan.FromSeconds(5));
		var closedBeforeCleanup = session.IsClosed;
		session.Close("test complete");
		await session.Completion;

		Assert.Multiple(() =>
		{
			Assert.That(results.Count(x => x.Result == ReplicationSendResult.Sent), Is.EqualTo(1));
			Assert.That(results.Count(x => x.Result == ReplicationSendResult.QueueFull), Is.EqualTo(1));
			Assert.That(retryResult, Is.EqualTo(ReplicationSendResult.Sent));
			Assert.That(closedBeforeCleanup, Is.False);
			Assert.That(writer.Messages, Has.Count.EqualTo(3));
		});
	}

	[Test]
	public async Task a_closed_session_rejects_send_without_blocking()
	{
		var writer = new BlockingStreamWriter();
		await using var session = new GrpcReplicationSession(
			ReplicationSessionIdentity.ForInsecureSystem(Guid.NewGuid()),
			Guid.NewGuid(), writer, 1, CancellationToken.None);
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		session.TrySend(new ReplicationMessage.ReplicaSubscribed(leaderId, subscriptionId, 10));
		await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		session.TrySend(new ReplicationMessage.FollowerAssignment(leaderId, subscriptionId));

		session.Close("test complete");
		var send = Task.Run(() =>
			session.TrySend(new ReplicationMessage.CloneAssignment(leaderId, subscriptionId)));
		var result = await send.WaitAsync(TimeSpan.FromSeconds(5));
		writer.Release.TrySetResult();

		await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Multiple(() =>
		{
			Assert.That(result, Is.EqualTo(ReplicationSendResult.Closed));
			Assert.That(session.IsClosed, Is.True);
			Assert.That(session.SendQueueSize, Is.Zero);
			Assert.That(session.GetStatistics().PendingSendBytes, Is.Zero);
		});
	}

	[Test]
	public async Task a_batch_is_admitted_atomically()
	{
		var writer = new BlockingStreamWriter(3);
		await using var session = new GrpcReplicationSession(
			ReplicationSessionIdentity.ForInsecureSystem(Guid.NewGuid()),
			Guid.NewGuid(), writer, 1, CancellationToken.None);
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		session.TrySend(new ReplicationMessage.ReplicaSubscribed(leaderId, subscriptionId, 10));
		await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Message[] batch =
		[
			new ReplicationMessage.FollowerAssignment(leaderId, subscriptionId),
			new ReplicationMessage.CloneAssignment(leaderId, subscriptionId)
		];

		var result = session.TrySend(batch);

		Assert.Multiple(() =>
		{
			Assert.That(result, Is.EqualTo(ReplicationSendResult.QueueFull));
			Assert.That(session.SendQueueSize, Is.EqualTo(1));
		});

		writer.Release.TrySetResult();
		AssertEx.IsOrBecomesTrue(() => session.SendQueueSize == 0);
		var retryResult = session.TrySend(batch);
		await writer.Written.WaitAsync(TimeSpan.FromSeconds(5));
		session.Close("test complete");
		await session.Completion;

		Assert.Multiple(() =>
		{
			Assert.That(retryResult, Is.EqualTo(ReplicationSendResult.Sent));
			Assert.That(writer.Messages, Has.Count.EqualTo(3));
		});
	}

	[Test]
	public async Task rejected_admission_does_not_advance_the_sent_position()
	{
		var writer = new BlockingStreamWriter(2);
		await using var session = new GrpcReplicationSession(
			ReplicationSessionIdentity.ForInsecureSystem(Guid.NewGuid()),
			Guid.NewGuid(), writer, 1, CancellationToken.None);
		var leaderId = Guid.NewGuid();
		var subscriptionId = Guid.NewGuid();
		session.TrySend(new ReplicationMessage.ReplicaSubscribed(leaderId, subscriptionId, 10));
		await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		session.TrySend(new ReplicationMessage.ReplicaSubscribed(leaderId, subscriptionId, 20));

		var result = session.TrySend(new ReplicationMessage.ReplicaSubscribed(leaderId, subscriptionId, 30));

		writer.Release.TrySetResult();
		await writer.Written.WaitAsync(TimeSpan.FromSeconds(5));
		AssertEx.IsOrBecomesTrue(() => session.SendQueueSize == 0);

		Assert.Multiple(() =>
		{
			Assert.That(result, Is.EqualTo(ReplicationSendResult.QueueFull));
			Assert.That(session.SentReplicationPosition, Is.EqualTo(20));
		});
	}

	private sealed class ObservingStreamWriter : IServerStreamWriter<Proto.LeaderFrame>
	{
		private readonly int _expectedCount;
		private int _activeWrites;
		private int _maxConcurrentWrites;
		private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public ConcurrentQueue<Proto.LeaderFrame> Messages { get; } = new();
		public Task Written => _written.Task;
		public int MaxConcurrentWrites => Volatile.Read(ref _maxConcurrentWrites);
		public WriteOptions WriteOptions { get; set; } = null!;

		public ObservingStreamWriter(int expectedCount = 3)
		{
			_expectedCount = expectedCount;
		}

		public async Task WriteAsync(Proto.LeaderFrame message)
		{
			var active = Interlocked.Increment(ref _activeWrites);
			InterlockedExtensions.Max(ref _maxConcurrentWrites, active);
			await Task.Yield();
			Messages.Enqueue(message);
			if (Messages.Count == _expectedCount)
			{
				_written.TrySetResult();
			}
			Interlocked.Decrement(ref _activeWrites);
		}
	}

	private static ChunkHeader ChunkHeaderForPositionTests(bool isScavenged) => new(
		TFChunk.CurrentChunkVersion,
		TFChunk.CurrentChunkVersion,
		1000,
		2,
		2,
		isScavenged,
		Guid.NewGuid(),
		TransformType.Identity);

	private sealed class BlockingStreamWriter : IServerStreamWriter<Proto.LeaderFrame>
	{
		private readonly int _expectedCount;
		private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public BlockingStreamWriter(int expectedCount = 0)
		{
			_expectedCount = expectedCount;
		}

		public ConcurrentQueue<Proto.LeaderFrame> Messages { get; } = new();
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task Written => _written.Task;
		public WriteOptions WriteOptions { get; set; } = null!;

		public async Task WriteAsync(Proto.LeaderFrame message)
		{
			Messages.Enqueue(message);
			Started.TrySetResult();
			await Release.Task;
			if (Messages.Count == _expectedCount)
			{
				_written.TrySetResult();
			}
		}
	}

	private static class InterlockedExtensions
	{
		public static void Max(ref int target, int value)
		{
			var current = Volatile.Read(ref target);
			while (current < value)
			{
				var observed = Interlocked.CompareExchange(ref target, value, current);
				if (observed == current)
				{
					return;
				}
				current = observed;
			}
		}
	}
}
