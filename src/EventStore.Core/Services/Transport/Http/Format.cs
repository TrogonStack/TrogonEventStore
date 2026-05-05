using System;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;

namespace EventStore.Core.Services.Transport.Http {
	public static class Format {
		public static string TextMessage(HttpResponseFormatterArgs entity, Message message) {
			var textMessage = message as HttpMessage.TextMessage;
			return textMessage != null ? entity.ResponseCodec.To(textMessage) : String.Empty;
		}

		public static string GetFreshStatsCompleted(HttpResponseFormatterArgs entity, Message message) {
			var completed = message as MonitoringMessage.GetFreshStatsCompleted;
			if (completed == null || !completed.Success)
				return String.Empty;

			return entity.ResponseCodec.To(completed.Stats);
		}

	}
}
