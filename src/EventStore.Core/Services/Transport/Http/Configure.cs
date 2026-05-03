using System;
using System.Collections.Generic;
using System.Text;
using EventStore.Common.Utils;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Monitoring;
using HttpStatusCode = EventStore.Transport.Http.HttpStatusCode;

namespace EventStore.Core.Services.Transport.Http {
	public static class Configure {
		public static bool DisableHTTPCaching = false;

		public static ResponseConfiguration Ok(string contentType) {
			return new ResponseConfiguration(HttpStatusCode.OK, "OK", contentType, Helper.UTF8NoBom);
		}

		public static ResponseConfiguration Ok(string contentType,
			Encoding encoding,
			string etag,
			int? cacheSeconds,
			bool isCachePublic,
			params KeyValuePair<string, string>[] headers) {
			var headrs = new List<KeyValuePair<string, string>>(headers);
			if (DisableHTTPCaching) {
				headrs.Add(new KeyValuePair<string, string>(
					"Cache-Control",
					"max-age=0, no-cache, must-revalidate"));
			} else {
				headrs.Add(new KeyValuePair<string, string>(
					"Cache-Control",
					cacheSeconds.HasValue
						? string.Format("max-age={0}, {1}", cacheSeconds, isCachePublic ? "public" : "private")
						: "max-age=0, no-cache, must-revalidate"));
			}

			headrs.Add(new KeyValuePair<string, string>("Vary", "Accept"));
			if (etag.IsNotEmptyString())
				headrs.Add(new KeyValuePair<string, string>("ETag", string.Format("\"{0}\"", etag)));
			return new ResponseConfiguration(HttpStatusCode.OK, "OK", contentType, encoding, headrs);
		}

		public static ResponseConfiguration TemporaryRedirect(Uri originalUrl, string targetHost, int targetPort) {
			var srcBase =
				new Uri(string.Format("{0}://{1}:{2}/", originalUrl.Scheme, originalUrl.Host, originalUrl.Port),
					UriKind.Absolute);
			var targetBase = new Uri(string.Format("{0}://{1}:{2}/", originalUrl.Scheme, targetHost, targetPort),
				UriKind.Absolute);
			var forwardUri = new Uri(targetBase, srcBase.MakeRelativeUri(originalUrl));
			return new ResponseConfiguration(HttpStatusCode.TemporaryRedirect, "Temporary Redirect", "text/plain",
				Helper.UTF8NoBom,
				new KeyValuePair<string, string>("Location", forwardUri.ToString()));
		}

		public static ResponseConfiguration DenyRequestBecauseReadOnly(Uri originalUrl, string targetHost, int targetPort) {
			var srcBase =
				new Uri(string.Format("{0}://{1}:{2}/", originalUrl.Scheme, originalUrl.Host, originalUrl.Port),
					UriKind.Absolute);
			var targetBase = new Uri(string.Format("{0}://{1}:{2}/", originalUrl.Scheme, targetHost, targetPort),
				UriKind.Absolute);
			var forwardUri = new Uri(targetBase, srcBase.MakeRelativeUri(originalUrl));
			return new ResponseConfiguration(HttpStatusCode.InternalServerError,
				"Operation Not Supported on Read Only Replica", "text/plain",
				Helper.UTF8NoBom,
				new KeyValuePair<string, string>("Location", forwardUri.ToString()));
		}

		public static ResponseConfiguration NotFound() {
			return new ResponseConfiguration(HttpStatusCode.NotFound, "Not Found", "text/plain", Helper.UTF8NoBom);
		}

		public static ResponseConfiguration NotFound(string etag, int? cacheSeconds, bool isCachePublic,
			string contentType) {
			var headrs = new List<KeyValuePair<string, string>>();
			headrs.Add(new KeyValuePair<string, string>(
				"Cache-Control",
				cacheSeconds.HasValue
					? string.Format("max-age={0}, {1}", cacheSeconds, isCachePublic ? "public" : "private")
					: "max-age=0, no-cache, must-revalidate"));
			headrs.Add(new KeyValuePair<string, string>("Vary", "Accept"));
			if (etag.IsNotEmptyString())
				headrs.Add(new KeyValuePair<string, string>("ETag", string.Format("\"{0}\"", etag)));
			return new ResponseConfiguration(HttpStatusCode.NotFound, "Not Found", contentType, Helper.UTF8NoBom,
				headrs);
		}

		public static ResponseConfiguration Gone(string description = null) {
			return new ResponseConfiguration(HttpStatusCode.Gone, description ?? "Deleted", "text/plain",
				Helper.UTF8NoBom);
		}

		public static ResponseConfiguration NotModified() {
			return new ResponseConfiguration(HttpStatusCode.NotModified, "Not Modified", "text/plain",
				Helper.UTF8NoBom);
		}

		public static ResponseConfiguration BadRequest(string description = null) {
			return new ResponseConfiguration(HttpStatusCode.BadRequest, description ?? "Bad Request", "text/plain",
				Helper.UTF8NoBom);
		}

		public static ResponseConfiguration InternalServerError(string description = null) {
			return new ResponseConfiguration(HttpStatusCode.InternalServerError, description ?? "Internal Server Error",
				"text/plain", Helper.UTF8NoBom);
		}

		public static ResponseConfiguration ServiceUnavailable(string description = null) {
			return new ResponseConfiguration(HttpStatusCode.ServiceUnavailable, description ?? "Service Unavailable",
				"text/plain", Helper.UTF8NoBom);
		}

		public static ResponseConfiguration NotImplemented(string description = null) {
			return new ResponseConfiguration(HttpStatusCode.NotImplemented, description ?? "Not Implemented",
				"text/plain", Helper.UTF8NoBom);
		}

		public static ResponseConfiguration Unauthorized(string description = null) {
			return new ResponseConfiguration(HttpStatusCode.Unauthorized, description ?? "Unauthorized", "text/plain",
				Helper.UTF8NoBom);
		}

		private static ResponseConfiguration HandleNotHandled(Uri requestedUri, ClientMessage.NotHandled notHandled) {
			switch (notHandled.Reason) {
				case ClientMessage.NotHandled.Types.NotHandledReason.NotReady:
					return ServiceUnavailable("Server Is Not Ready");
				case ClientMessage.NotHandled.Types.NotHandledReason.TooBusy:
					return ServiceUnavailable("Server Is Too Busy");
				case ClientMessage.NotHandled.Types.NotHandledReason.NotLeader: {
					var leaderInfo = notHandled.LeaderInfo;
					if (leaderInfo == null)
						return InternalServerError("No leader info available in response");
					return TemporaryRedirect(requestedUri, leaderInfo.Http.GetHost(), leaderInfo.Http.GetPort());
				}
				case ClientMessage.NotHandled.Types.NotHandledReason.IsReadOnly: {
					var leaderInfo = notHandled.LeaderInfo;
					if (leaderInfo == null)
						return InternalServerError("No leader info available in response");
					return DenyRequestBecauseReadOnly(requestedUri, leaderInfo.Http.GetHost(), leaderInfo.Http.GetPort());
				}
				default:
					return InternalServerError(string.Format("Unknown not handled reason: {0}", notHandled.Reason));
			}
		}

		public static ResponseConfiguration
			GetFreshStatsCompleted(HttpResponseConfiguratorArgs entity, Message message) {
			var completed = message as MonitoringMessage.GetFreshStatsCompleted;
			if (completed == null)
				return InternalServerError();

			var cacheSeconds = (int)MonitoringService.MemoizePeriod.TotalSeconds;
			return completed.Success
				? Ok(entity.ResponseCodec.ContentType, Helper.UTF8NoBom, null, cacheSeconds, isCachePublic: true)
				: NotFound();
		}

		public static ResponseConfiguration GetReplicationStatsCompleted(HttpResponseConfiguratorArgs entity,
			Message message) {
			var completed = message as ReplicationMessage.GetReplicationStatsCompleted;
			if (completed == null)
				return InternalServerError();

			var cacheSeconds = (int)MonitoringService.MemoizePeriod.TotalSeconds;
			return Ok(entity.ResponseCodec.ContentType, Helper.UTF8NoBom, null, cacheSeconds, isCachePublic: true);
		}

		public static ResponseConfiguration GetFreshTcpConnectionStatsCompleted(HttpResponseConfiguratorArgs entity,
			Message message) {
			var completed = message as MonitoringMessage.GetFreshTcpConnectionStatsCompleted;
			if (completed == null)
				return InternalServerError();

			var cacheSeconds = (int)MonitoringService.MemoizePeriod.TotalSeconds;
			return Ok(entity.ResponseCodec.ContentType, Helper.UTF8NoBom, null, cacheSeconds, isCachePublic: true);
		}
	}
}
