using System;
using EventStore.Common.Utils;
using EventStore.Core.TransactionLog.Chunks;

namespace EventStore.Core.Data
{
	public class Event
	{
		public readonly Guid EventId;
		public readonly string EventType;
		public readonly bool IsJson;
		public readonly byte[] Data;
		public readonly bool IsPropertyMetadata;
		public readonly byte[] Metadata;

		public Event(Guid eventId, string eventType, bool isJson, string data, string metadata)
			: this(
				eventId, eventType, isJson, Helper.UTF8NoBom.GetBytes(data),
				isPropertyMetadata: false,
				metadata != null ? Helper.UTF8NoBom.GetBytes(metadata) : null)
		{
		}

		public static int SizeOnDisk(string eventType, byte[] data, byte[] metadata) =>
			(data?.Length ?? 0) + (metadata?.Length ?? 0) + (eventType?.Length ?? 0) * 2;

		private static bool ExceedsMaximumSizeOnDisk(string eventType, byte[] data, byte[] metadata) =>
			SizeOnDisk(eventType, data, metadata) > TFConsts.EffectiveMaxLogRecordSize;

		public Event(Guid eventId, string eventType, bool isJson, byte[] data, byte[] metadata)
			: this(eventId, eventType, isJson, data, isPropertyMetadata: false, metadata)
		{
		}

		public Event(Guid eventId, string eventType, bool isJson, byte[] data, bool isPropertyMetadata, byte[] metadata)
		{
			if (eventId == Guid.Empty)
			{
				throw new ArgumentException("Empty eventId provided.", nameof(eventId));
			}

			if (string.IsNullOrEmpty(eventType))
			{
				throw new ArgumentException("Empty eventType provided.", nameof(eventType));
			}

			if (ExceedsMaximumSizeOnDisk(eventType, data, metadata))
			{
				throw new ArgumentException("Record is too big.", nameof(data));
			}

			EventId = eventId;
			EventType = eventType;
			IsJson = isJson;
			Data = data ?? Array.Empty<byte>();
			IsPropertyMetadata = isPropertyMetadata;
			Metadata = metadata ?? Array.Empty<byte>();
		}
	}
}
