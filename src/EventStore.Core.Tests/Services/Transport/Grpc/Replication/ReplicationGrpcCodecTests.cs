using System;
using System.Linq;
using System.Net;
using EventStore.Core.Messages;
using EventStore.Core.Services.Transport.Grpc.Replication;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Plugins.Transforms;
using Google.Protobuf;
using NUnit.Framework;
using Proto = EventStore.Replication;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Replication;

[TestFixture]
public class ReplicationGrpcCodecTests
{
	[Test]
	public void replicate_is_bidirectional_streaming()
	{
		var method = Proto.Replication.Descriptor.Methods.Single();

		Assert.Multiple(() =>
		{
			Assert.That(method.Name, Is.EqualTo("Replicate"));
			Assert.That(method.IsClientStreaming, Is.True);
			Assert.That(method.IsServerStreaming, Is.True);
		});
	}

	[Test]
	public void subscribe_replica_round_trips()
	{
		var epoch = new EpochRecord(100, 3, Guid.NewGuid(), 50, DateTime.UtcNow, Guid.NewGuid());
		var replicaInstanceId = Guid.NewGuid();
		var message = new ReplicationMessage.SubscribeReplica(
			2,
			200,
			Guid.NewGuid(),
			new[] { epoch },
			new DnsEndPoint("replica.internal", 2113),
			Guid.NewGuid(),
			Guid.NewGuid(),
			true,
			replicaInstanceId);

		var frame = RoundTrip(ReplicationGrpcCodec.ToGrpc(message));
		var decoded = (ReplicationMessage.SubscribeReplica)ReplicationGrpcCodec.FromGrpc(frame);

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.ReplicaFrame.PayloadOneofCase.Subscribe));
			Assert.That(decoded.Version, Is.EqualTo(message.Version));
			Assert.That(decoded.LogPosition, Is.EqualTo(message.LogPosition));
			Assert.That(decoded.ChunkId, Is.EqualTo(message.ChunkId));
			Assert.That(decoded.LastEpochs.Single().EpochPosition, Is.EqualTo(epoch.EpochPosition));
			Assert.That(decoded.LastEpochs.Single().EpochNumber, Is.EqualTo(epoch.EpochNumber));
			Assert.That(decoded.LastEpochs.Single().EpochId, Is.EqualTo(epoch.EpochId));
			Assert.That(((DnsEndPoint)decoded.ReplicaEndPoint).Host, Is.EqualTo("replica.internal"));
			Assert.That(((DnsEndPoint)decoded.ReplicaEndPoint).Port, Is.EqualTo(2113));
			Assert.That(decoded.LeaderId, Is.EqualTo(message.LeaderId));
			Assert.That(decoded.SubscriptionId, Is.EqualTo(message.SubscriptionId));
			Assert.That(decoded.IsPromotable, Is.True);
			Assert.That(decoded.ReplicaInstanceId, Is.EqualTo(replicaInstanceId));
		});
	}

	[Test]
	public void acknowledgement_round_trips_to_received_acknowledgement()
	{
		var message = new ReplicationMessage.AckLogPosition(Guid.NewGuid(), 300, 250);

		var frame = RoundTrip(ReplicationGrpcCodec.ToGrpc(message));
		var decoded = (ReplicationMessage.ReplicaLogPositionAck)ReplicationGrpcCodec.FromGrpc(frame);

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.ReplicaFrame.PayloadOneofCase.Acknowledgement));
			Assert.That(decoded.SubscriptionId, Is.EqualTo(message.SubscriptionId));
			Assert.That(decoded.ReplicationLogPosition, Is.EqualTo(message.ReplicationLogPosition));
			Assert.That(decoded.WriterLogPosition, Is.EqualTo(message.WriterLogPosition));
		});
	}

	[Test]
	public void retry_round_trips()
	{
		var message = new ReplicationMessage.ReplicaSubscriptionRetry(Guid.NewGuid(), Guid.NewGuid());

		var frame = RoundTrip(ReplicationGrpcCodec.ToGrpc(message));
		var decoded = (ReplicationMessage.ReplicaSubscriptionRetry)ReplicationGrpcCodec.FromGrpc(frame);

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.LeaderFrame.PayloadOneofCase.Retry));
			Assert.That(decoded.LeaderId, Is.EqualTo(message.LeaderId));
			Assert.That(decoded.SubscriptionId, Is.EqualTo(message.SubscriptionId));
		});
	}

	[Test]
	public void subscribed_round_trips()
	{
		var message = new ReplicationMessage.ReplicaSubscribed(Guid.NewGuid(), Guid.NewGuid(), 400);

		var frame = RoundTrip(ReplicationGrpcCodec.ToGrpc(message));
		var decoded = (ReplicationMessage.ReplicaSubscribed)ReplicationGrpcCodec.FromGrpc(frame);

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.LeaderFrame.PayloadOneofCase.Subscribed));
			Assert.That(decoded.LeaderId, Is.EqualTo(message.LeaderId));
			Assert.That(decoded.SubscriptionId, Is.EqualTo(message.SubscriptionId));
			Assert.That(decoded.SubscriptionPosition, Is.EqualTo(message.SubscriptionPosition));
		});
	}

	[Test]
	public void create_chunk_round_trips()
	{
		var chunkHeader = new ChunkHeader(
			TFChunk.CurrentChunkVersion,
			TFChunk.CurrentChunkVersion,
			4096,
			1,
			1,
			true,
			Guid.NewGuid(),
			TransformType.Identity);
		var message = new ReplicationMessage.CreateChunk(
			Guid.NewGuid(), Guid.NewGuid(), chunkHeader, 8192, true, new byte[] { 1, 2, 3 });

		var frame = RoundTrip(ReplicationGrpcCodec.ToGrpc(message));
		var decoded = (ReplicationMessage.CreateChunk)ReplicationGrpcCodec.FromGrpc(frame);

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.LeaderFrame.PayloadOneofCase.CreateChunk));
			Assert.That(decoded.LeaderId, Is.EqualTo(message.LeaderId));
			Assert.That(decoded.SubscriptionId, Is.EqualTo(message.SubscriptionId));
			Assert.That(decoded.ChunkHeader.AsByteArray(), Is.EqualTo(message.ChunkHeader.AsByteArray()));
			Assert.That(decoded.FileSize, Is.EqualTo(message.FileSize));
			Assert.That(decoded.IsScavengedChunk, Is.EqualTo(message.IsScavengedChunk));
			Assert.That(decoded.TransformHeader.ToArray(), Is.EqualTo(message.TransformHeader.ToArray()));
		});
	}

	[Test]
	public void raw_chunk_bulk_round_trips()
	{
		var message = new ReplicationMessage.RawChunkBulk(
			Guid.NewGuid(), Guid.NewGuid(), 1, 2, 128, new byte[] { 4, 5, 6 }, true);

		var frame = RoundTrip(ReplicationGrpcCodec.ToGrpc(message));
		var decoded = (ReplicationMessage.RawChunkBulk)ReplicationGrpcCodec.FromGrpc(frame);

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.LeaderFrame.PayloadOneofCase.RawChunkBulk));
			Assert.That(decoded.LeaderId, Is.EqualTo(message.LeaderId));
			Assert.That(decoded.SubscriptionId, Is.EqualTo(message.SubscriptionId));
			Assert.That(decoded.ChunkStartNumber, Is.EqualTo(message.ChunkStartNumber));
			Assert.That(decoded.ChunkEndNumber, Is.EqualTo(message.ChunkEndNumber));
			Assert.That(decoded.RawPosition, Is.EqualTo(message.RawPosition));
			Assert.That(decoded.RawBytes, Is.EqualTo(message.RawBytes));
			Assert.That(decoded.CompleteChunk, Is.EqualTo(message.CompleteChunk));
		});
	}

	[Test]
	public void data_chunk_bulk_round_trips()
	{
		var message = new ReplicationMessage.DataChunkBulk(
			Guid.NewGuid(), Guid.NewGuid(), 2, 3, 512, new byte[] { 7, 8, 9 }, true);

		var frame = RoundTrip(ReplicationGrpcCodec.ToGrpc(message));
		var decoded = (ReplicationMessage.DataChunkBulk)ReplicationGrpcCodec.FromGrpc(frame);

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.LeaderFrame.PayloadOneofCase.DataChunkBulk));
			Assert.That(decoded.LeaderId, Is.EqualTo(message.LeaderId));
			Assert.That(decoded.SubscriptionId, Is.EqualTo(message.SubscriptionId));
			Assert.That(decoded.ChunkStartNumber, Is.EqualTo(message.ChunkStartNumber));
			Assert.That(decoded.ChunkEndNumber, Is.EqualTo(message.ChunkEndNumber));
			Assert.That(decoded.SubscriptionPosition, Is.EqualTo(message.SubscriptionPosition));
			Assert.That(decoded.DataBytes, Is.EqualTo(message.DataBytes));
			Assert.That(decoded.CompleteChunk, Is.EqualTo(message.CompleteChunk));
		});
	}

	[Test]
	public void bulk_payloads_are_isolated_from_general_codec_callers()
	{
		var rawBytes = new byte[] { 1, 2, 3 };
		var dataBytes = new byte[] { 4, 5, 6 };
		var rawFrame = ReplicationGrpcCodec.ToGrpc(new ReplicationMessage.RawChunkBulk(
			Guid.NewGuid(), Guid.NewGuid(), 1, 2, 10, rawBytes, false));
		var dataFrame = ReplicationGrpcCodec.ToGrpc(new ReplicationMessage.DataChunkBulk(
			Guid.NewGuid(), Guid.NewGuid(), 1, 2, 20, dataBytes, false));

		rawBytes[0] = 9;
		dataBytes[0] = 9;

		Assert.Multiple(() =>
		{
			Assert.That(rawFrame.RawChunkBulk.RawBytes.ToByteArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
			Assert.That(dataFrame.DataChunkBulk.DataBytes.ToByteArray(), Is.EqualTo(new byte[] { 4, 5, 6 }));
		});
	}

	[Test]
	public void follower_assignment_round_trips()
	{
		var message = new ReplicationMessage.FollowerAssignment(Guid.NewGuid(), Guid.NewGuid());

		var frame = RoundTrip(ReplicationGrpcCodec.ToGrpc(message));
		var decoded = (ReplicationMessage.FollowerAssignment)ReplicationGrpcCodec.FromGrpc(frame);

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.LeaderFrame.PayloadOneofCase.FollowerAssignment));
			Assert.That(decoded.LeaderId, Is.EqualTo(message.LeaderId));
			Assert.That(decoded.SubscriptionId, Is.EqualTo(message.SubscriptionId));
		});
	}

	[Test]
	public void clone_assignment_round_trips()
	{
		var message = new ReplicationMessage.CloneAssignment(Guid.NewGuid(), Guid.NewGuid());

		var frame = RoundTrip(ReplicationGrpcCodec.ToGrpc(message));
		var decoded = (ReplicationMessage.CloneAssignment)ReplicationGrpcCodec.FromGrpc(frame);

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.LeaderFrame.PayloadOneofCase.CloneAssignment));
			Assert.That(decoded.LeaderId, Is.EqualTo(message.LeaderId));
			Assert.That(decoded.SubscriptionId, Is.EqualTo(message.SubscriptionId));
		});
	}

	[Test]
	public void drop_subscription_round_trips()
	{
		var message = new ReplicationMessage.DropSubscription(Guid.NewGuid(), Guid.NewGuid());

		var frame = RoundTrip(ReplicationGrpcCodec.ToGrpc(message));
		var decoded = (ReplicationMessage.DropSubscription)ReplicationGrpcCodec.FromGrpc(frame);

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.LeaderFrame.PayloadOneofCase.DropSubscription));
			Assert.That(decoded.LeaderId, Is.EqualTo(message.LeaderId));
			Assert.That(decoded.SubscriptionId, Is.EqualTo(message.SubscriptionId));
		});
	}

	[Test]
	public void replicated_to_round_trips_to_follower_notification()
	{
		var message = new ReplicationTrackingMessage.ReplicatedTo(1024);

		var frame = RoundTrip(ReplicationGrpcCodec.ToGrpc(message));
		var decoded = (ReplicationTrackingMessage.LeaderReplicatedTo)ReplicationGrpcCodec.FromGrpc(frame);

		Assert.Multiple(() =>
		{
			Assert.That(frame.PayloadCase, Is.EqualTo(Proto.LeaderFrame.PayloadOneofCase.ReplicatedTo));
			Assert.That(decoded.LogPosition, Is.EqualTo(message.LogPosition));
		});
	}

	private static Proto.ReplicaFrame RoundTrip(Proto.ReplicaFrame frame) =>
		Proto.ReplicaFrame.Parser.ParseFrom(frame.ToByteArray());

	private static Proto.LeaderFrame RoundTrip(Proto.LeaderFrame frame) =>
		Proto.LeaderFrame.Parser.ParseFrom(frame.ToByteArray());
}
