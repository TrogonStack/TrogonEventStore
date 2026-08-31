using System;
using System.Linq;
using System.Net;
using EventStore.Common.Utils;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.LogRecords;
using Google.Protobuf;
using Proto = EventStore.Replication;

namespace EventStore.Core.Services.Transport.Grpc.Replication;

public static class ReplicationGrpcCodec
{
	public static Proto.ReplicaFrame ToGrpc(ReplicationMessage.SubscribeReplica message)
	{
		var subscribe = new Proto.SubscribeReplica
		{
			Version = message.Version,
			LogPosition = message.LogPosition,
			ChunkId = Uuid.FromGuid(message.ChunkId).ToDto(),
			AdvertisedEndpoint = new Proto.GrpcEndPoint
			{
				Address = message.ReplicaEndPoint.GetHost(),
				Port = checked((uint)message.ReplicaEndPoint.GetPort())
			},
			LeaderId = Uuid.FromGuid(message.LeaderId).ToDto(),
			SubscriptionId = Uuid.FromGuid(message.SubscriptionId).ToDto(),
			IsPromotable = message.IsPromotable,
			ReplicaInstanceId = Uuid.FromGuid(message.ReplicaInstanceId).ToDto()
		};
		subscribe.LastEpochs.Add(message.LastEpochs.Select(ToGrpc));

		return new Proto.ReplicaFrame { Subscribe = subscribe };
	}

	public static Proto.ReplicaFrame ToGrpc(ReplicationMessage.AckLogPosition message) => new()
	{
		Acknowledgement = new Proto.ReplicaLogPositionAck
		{
			SubscriptionId = Uuid.FromGuid(message.SubscriptionId).ToDto(),
			ReplicationLogPosition = message.ReplicationLogPosition,
			WriterLogPosition = message.WriterLogPosition
		}
	};

	public static Message FromGrpc(Proto.ReplicaFrame frame) => frame.PayloadCase switch
	{
		Proto.ReplicaFrame.PayloadOneofCase.Subscribe => FromGrpc(frame.Subscribe),
		Proto.ReplicaFrame.PayloadOneofCase.Acknowledgement => FromGrpc(frame.Acknowledgement),
		_ => throw new ArgumentOutOfRangeException(nameof(frame), frame.PayloadCase, "Unknown replica frame")
	};

	public static Proto.LeaderFrame ToGrpc(ReplicationMessage.ReplicaSubscriptionRetry message) => new()
	{
		Retry = new Proto.ReplicaSubscriptionRetry
		{
			LeaderId = Uuid.FromGuid(message.LeaderId).ToDto(),
			SubscriptionId = Uuid.FromGuid(message.SubscriptionId).ToDto()
		}
	};

	public static Proto.LeaderFrame ToGrpc(ReplicationMessage.ReplicaSubscribed message) => new()
	{
		Subscribed = new Proto.ReplicaSubscribed
		{
			LeaderId = Uuid.FromGuid(message.LeaderId).ToDto(),
			SubscriptionId = Uuid.FromGuid(message.SubscriptionId).ToDto(),
			SubscriptionPosition = message.SubscriptionPosition
		}
	};

	public static Proto.LeaderFrame ToGrpc(ReplicationMessage.CreateChunk message) => new()
	{
		CreateChunk = new Proto.CreateChunk
		{
			LeaderId = Uuid.FromGuid(message.LeaderId).ToDto(),
			SubscriptionId = Uuid.FromGuid(message.SubscriptionId).ToDto(),
			ChunkHeaderBytes = ByteString.CopyFrom(message.ChunkHeader.AsByteArray()),
			FileSize = message.FileSize,
			IsScavengedChunk = message.IsScavengedChunk,
			TransformHeaderBytes = ByteString.CopyFrom(message.TransformHeader.Span)
		}
	};

	public static Proto.LeaderFrame ToGrpc(ReplicationMessage.RawChunkBulk message) => new()
	{
		RawChunkBulk = ToGrpc(message, ByteString.CopyFrom(message.RawBytes))
	};

	internal static Proto.LeaderFrame ToGrpcOwned(ReplicationMessage.RawChunkBulk message) => new()
	{
		RawChunkBulk = ToGrpc(message, UnsafeByteOperations.UnsafeWrap(message.RawBytes))
	};

	private static Proto.RawChunkBulk ToGrpc(ReplicationMessage.RawChunkBulk message, ByteString rawBytes) => new()
	{
		LeaderId = Uuid.FromGuid(message.LeaderId).ToDto(),
		SubscriptionId = Uuid.FromGuid(message.SubscriptionId).ToDto(),
		ChunkStartNumber = message.ChunkStartNumber,
		ChunkEndNumber = message.ChunkEndNumber,
		RawPosition = message.RawPosition,
		RawBytes = rawBytes,
		CompleteChunk = message.CompleteChunk
	};

	public static Proto.LeaderFrame ToGrpc(ReplicationMessage.DataChunkBulk message) => new()
	{
		DataChunkBulk = ToGrpc(message, ByteString.CopyFrom(message.DataBytes))
	};

	internal static Proto.LeaderFrame ToGrpcOwned(ReplicationMessage.DataChunkBulk message) => new()
	{
		DataChunkBulk = ToGrpc(message, UnsafeByteOperations.UnsafeWrap(message.DataBytes))
	};

	private static Proto.DataChunkBulk ToGrpc(ReplicationMessage.DataChunkBulk message, ByteString dataBytes) => new()
	{
		LeaderId = Uuid.FromGuid(message.LeaderId).ToDto(),
		SubscriptionId = Uuid.FromGuid(message.SubscriptionId).ToDto(),
		ChunkStartNumber = message.ChunkStartNumber,
		ChunkEndNumber = message.ChunkEndNumber,
		SubscriptionPosition = message.SubscriptionPosition,
		DataBytes = dataBytes,
		CompleteChunk = message.CompleteChunk
	};

	public static Proto.LeaderFrame ToGrpc(ReplicationMessage.FollowerAssignment message) => new()
	{
		FollowerAssignment = new Proto.FollowerAssignment
		{
			LeaderId = Uuid.FromGuid(message.LeaderId).ToDto(),
			SubscriptionId = Uuid.FromGuid(message.SubscriptionId).ToDto()
		}
	};

	public static Proto.LeaderFrame ToGrpc(ReplicationMessage.CloneAssignment message) => new()
	{
		CloneAssignment = new Proto.CloneAssignment
		{
			LeaderId = Uuid.FromGuid(message.LeaderId).ToDto(),
			SubscriptionId = Uuid.FromGuid(message.SubscriptionId).ToDto()
		}
	};

	public static Proto.LeaderFrame ToGrpc(ReplicationMessage.DropSubscription message) => new()
	{
		DropSubscription = new Proto.DropSubscription
		{
			LeaderId = Uuid.FromGuid(message.LeaderId).ToDto(),
			SubscriptionId = Uuid.FromGuid(message.SubscriptionId).ToDto()
		}
	};

	public static Proto.LeaderFrame ToGrpc(ReplicationTrackingMessage.ReplicatedTo message) => new()
	{
		ReplicatedTo = new Proto.ReplicatedTo { LogPosition = message.LogPosition }
	};

	public static Message FromGrpc(Proto.LeaderFrame frame) => frame.PayloadCase switch
	{
		Proto.LeaderFrame.PayloadOneofCase.Retry => FromGrpc(frame.Retry),
		Proto.LeaderFrame.PayloadOneofCase.Subscribed => FromGrpc(frame.Subscribed),
		Proto.LeaderFrame.PayloadOneofCase.CreateChunk => FromGrpc(frame.CreateChunk),
		Proto.LeaderFrame.PayloadOneofCase.RawChunkBulk => FromGrpc(frame.RawChunkBulk),
		Proto.LeaderFrame.PayloadOneofCase.DataChunkBulk => FromGrpc(frame.DataChunkBulk),
		Proto.LeaderFrame.PayloadOneofCase.FollowerAssignment => FromGrpc(frame.FollowerAssignment),
		Proto.LeaderFrame.PayloadOneofCase.CloneAssignment => FromGrpc(frame.CloneAssignment),
		Proto.LeaderFrame.PayloadOneofCase.DropSubscription => FromGrpc(frame.DropSubscription),
		Proto.LeaderFrame.PayloadOneofCase.ReplicatedTo => FromGrpc(frame.ReplicatedTo),
		_ => throw new ArgumentOutOfRangeException(nameof(frame), frame.PayloadCase, "Unknown leader frame")
	};

	private static Proto.Epoch ToGrpc(EpochRecord epoch) => new()
	{
		EpochPosition = epoch.EpochPosition,
		EpochNumber = epoch.EpochNumber,
		EpochId = Uuid.FromGuid(epoch.EpochId).ToDto()
	};

	private static ReplicationMessage.SubscribeReplica FromGrpc(Proto.SubscribeReplica message) => new(
		message.Version,
		message.LogPosition,
		Uuid.FromDto(message.ChunkId).ToGuid(),
		message.LastEpochs.Select(FromGrpc).ToArray(),
		new DnsEndPoint(message.AdvertisedEndpoint.Address, checked((int)message.AdvertisedEndpoint.Port)),
		Uuid.FromDto(message.LeaderId).ToGuid(),
		Uuid.FromDto(message.SubscriptionId).ToGuid(),
		message.IsPromotable,
		Uuid.FromDto(message.ReplicaInstanceId).ToGuid());

	private static EpochRecord FromGrpc(Proto.Epoch epoch) => new(
		epoch.EpochPosition,
		epoch.EpochNumber,
		Uuid.FromDto(epoch.EpochId).ToGuid(),
		-1,
		DateTime.MinValue,
		Guid.Empty);

	private static ReplicationMessage.ReplicaLogPositionAck FromGrpc(Proto.ReplicaLogPositionAck message) => new(
		Uuid.FromDto(message.SubscriptionId).ToGuid(),
		message.ReplicationLogPosition,
		message.WriterLogPosition);

	private static ReplicationMessage.ReplicaSubscriptionRetry FromGrpc(Proto.ReplicaSubscriptionRetry message) => new(
		Uuid.FromDto(message.LeaderId).ToGuid(),
		Uuid.FromDto(message.SubscriptionId).ToGuid());

	private static ReplicationMessage.ReplicaSubscribed FromGrpc(Proto.ReplicaSubscribed message) => new(
		Uuid.FromDto(message.LeaderId).ToGuid(),
		Uuid.FromDto(message.SubscriptionId).ToGuid(),
		message.SubscriptionPosition);

	private static ReplicationMessage.CreateChunk FromGrpc(Proto.CreateChunk message) => new(
		Uuid.FromDto(message.LeaderId).ToGuid(),
		Uuid.FromDto(message.SubscriptionId).ToGuid(),
		new ChunkHeader(message.ChunkHeaderBytes.Span),
		message.FileSize,
		message.IsScavengedChunk,
		message.TransformHeaderBytes.Memory);

	private static ReplicationMessage.RawChunkBulk FromGrpc(Proto.RawChunkBulk message) => new(
		Uuid.FromDto(message.LeaderId).ToGuid(),
		Uuid.FromDto(message.SubscriptionId).ToGuid(),
		message.ChunkStartNumber,
		message.ChunkEndNumber,
		message.RawPosition,
		message.RawBytes.ToByteArray(),
		message.CompleteChunk);

	private static ReplicationMessage.DataChunkBulk FromGrpc(Proto.DataChunkBulk message) => new(
		Uuid.FromDto(message.LeaderId).ToGuid(),
		Uuid.FromDto(message.SubscriptionId).ToGuid(),
		message.ChunkStartNumber,
		message.ChunkEndNumber,
		message.SubscriptionPosition,
		message.DataBytes.ToByteArray(),
		message.CompleteChunk);

	private static ReplicationMessage.FollowerAssignment FromGrpc(Proto.FollowerAssignment message) => new(
		Uuid.FromDto(message.LeaderId).ToGuid(),
		Uuid.FromDto(message.SubscriptionId).ToGuid());

	private static ReplicationMessage.CloneAssignment FromGrpc(Proto.CloneAssignment message) => new(
		Uuid.FromDto(message.LeaderId).ToGuid(),
		Uuid.FromDto(message.SubscriptionId).ToGuid());

	private static ReplicationMessage.DropSubscription FromGrpc(Proto.DropSubscription message) => new(
		Uuid.FromDto(message.LeaderId).ToGuid(),
		Uuid.FromDto(message.SubscriptionId).ToGuid());

	private static ReplicationTrackingMessage.LeaderReplicatedTo FromGrpc(Proto.ReplicatedTo message) =>
		new(message.LogPosition);
}
