using System;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Cluster;

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

		public static string GetReplicationStatsCompleted(HttpResponseFormatterArgs entity, Message message) {
			if (message.GetType() != typeof(ReplicationMessage.GetReplicationStatsCompleted))
				throw new Exception(string.Format("Unexpected type of Response message: {0}, expected: {1}",
					message.GetType().Name,
					typeof(ReplicationMessage.GetReplicationStatsCompleted).Name));
			var completed = message as ReplicationMessage.GetReplicationStatsCompleted;
			return entity.ResponseCodec.To(completed.ReplicationStats);
		}

		public static string GetFreshTcpConnectionStatsCompleted(HttpResponseFormatterArgs entity, Message message) {
			var completed = message as MonitoringMessage.GetFreshTcpConnectionStatsCompleted;
			if (completed == null)
				return String.Empty;

			return entity.ResponseCodec.To(completed.ConnectionStats);
		}

		public static string SendPublicGossip(HttpResponseFormatterArgs entity, Message message) {
			if (message.GetType() != typeof(GossipMessage.SendClientGossip))
				throw new Exception(string.Format("Unexpected type of response message: {0}, expected: {1}",
					message.GetType().Name,
					typeof(GossipMessage.SendClientGossip).Name));

			var sendPublicGossip = message as GossipMessage.SendClientGossip;
			return sendPublicGossip != null
				? entity.ResponseCodec.To(sendPublicGossip.ClusterInfo)
				: string.Empty;
		}
	}
}
