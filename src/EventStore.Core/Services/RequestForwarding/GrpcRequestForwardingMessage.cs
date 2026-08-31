using System;
using EventStore.Core.Messaging;

namespace EventStore.Core.Services.RequestForwarding;

public static partial class GrpcRequestForwardingMessage
{
	[DerivedMessage(CoreMessage.Grpc)]
	public sealed partial class Reconnect(Guid leaderId, long connectionGeneration) : Message
	{
		public Guid LeaderId { get; } = leaderId;
		public long ConnectionGeneration { get; } = connectionGeneration;
	}

	[DerivedMessage(CoreMessage.Grpc)]
	public sealed partial class StreamClosed(Guid leaderId, long connectionGeneration) : Message
	{
		public Guid LeaderId { get; } = leaderId;
		public long ConnectionGeneration { get; } = connectionGeneration;
	}
}
