using System;
using EventStore.Core.Data;
using EventStore.Core.Messages;

namespace EventStore.Core.Services.PersistentSubscription
{
	public interface IPersistentSubscriptionMessageParker
	{
		void RecordParkMessageRequest(ParkReason parkReason);
		void RecordParkedMessageReplay();
		void RecordParkedMessageTruncate();
		void BeginParkMessage(ResolvedEvent ev, string reason, Action<ResolvedEvent, OperationResult> completed);
		void BeginReadEndSequence(Action<ParkedStreamEndReadResult> completed);
		void BeginMarkParkedMessagesReprocessed(long sequence, DateTime? oldestParkedMessageTimestamp, bool updateOldestParkedMessage);
		void BeginMarkParkedMessagesTruncated(long sequence, Action<OperationResult> completed);
		void BeginDelete(Action<IPersistentSubscriptionMessageParker> completed);
		long ParkedMessageCount { get; }
		public void BeginLoadStats(Action completed);
		DateTime? GetOldestParkedMessage { get; }
		long ParkedDueToClientNak { get; }
		long ParkedDueToMaxRetries { get; }
		long ParkedMessageReplays { get; }
		long ParkedMessageTruncates { get; }
	}

	public readonly record struct ParkedStreamEndReadResult(long? LastEventNumber, string Error)
	{
		public bool Succeeded => Error is null;

		public static ParkedStreamEndReadResult Success(long? lastEventNumber) => new(lastEventNumber, null);

		public static ParkedStreamEndReadResult Failure(string error) => new(null, error);
	}

	public enum ParkReason
	{
		Unknown,
		ClientNak,
		MaxRetries
	}
}
