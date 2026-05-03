using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Transport.Http;
using EventStore.Transport.Http.Codecs;
using EventStore.Transport.Http.EntityManagement;
using EventStore.Core.Services.PersistentSubscription;
using EventStore.Common.Utils;
using EventStore.Plugins.Authorization;
using EventStore.Core.Settings;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.Services.Transport.Http.Controllers {
	public class PersistentSubscriptionController : CommunicationController {
		private readonly IHttpForwarder _httpForwarder;
		private readonly IPublisher _networkSendQueue;
		private static readonly ICodec[] DefaultCodecs = {Codec.Json, Codec.Xml};

		private static readonly ILogger Log = Serilog.Log.ForContext<PersistentSubscriptionController>();

		public PersistentSubscriptionController(IHttpForwarder httpForwarder, IPublisher publisher,
			IPublisher networkSendQueue)
			: base(publisher) {
			_httpForwarder = httpForwarder;
			_networkSendQueue = networkSendQueue;
		}

		protected override void SubscribeCore(IHttpService service) {
			Register(service, "/subscriptions?offset={offset}&count={count}", HttpMethod.Get, GetAllSubscriptionInfo, Codec.NoCodecs, DefaultCodecs, new Operation(Operations.Subscriptions.Statistics));
			Register(service, "/subscriptions", HttpMethod.Get, GetAllSubscriptionInfo, Codec.NoCodecs, DefaultCodecs, new Operation(Operations.Subscriptions.Statistics));
			Register(service, "/subscriptions/{stream}/{subscription}", HttpMethod.Post, PostSubscription,
				DefaultCodecs, DefaultCodecs, new Operation(Operations.Subscriptions.Update));
		}

		private void PostSubscription(HttpEntityManager http, UriTemplateMatch match) {
			if (_httpForwarder.ForwardRequest(http))
				return;
			var groupname = match.BoundVariables["subscription"];
			var stream = match.BoundVariables["stream"];
			var envelope = new SendToHttpEnvelope(
				_networkSendQueue, http,
				(args, message) => http.ResponseCodec.To(message),
				(args, message) => {
					int code;
					if (message is ClientMessage.NotHandled notHandled)
						return Configure.HandleNotHandled(args.RequestedUrl, notHandled);

					var m = message as ClientMessage.UpdatePersistentSubscriptionToStreamCompleted;
					if (m == null) throw new Exception("unexpected message " + message);
					switch (m.Result) {
						case ClientMessage.UpdatePersistentSubscriptionToStreamCompleted.UpdatePersistentSubscriptionToStreamResult
							.Success:
							code = HttpStatusCode.OK;
							//TODO competing return uri to subscription
							break;
						case ClientMessage.UpdatePersistentSubscriptionToStreamCompleted.UpdatePersistentSubscriptionToStreamResult
							.DoesNotExist:
							code = HttpStatusCode.NotFound;
							break;
						case ClientMessage.UpdatePersistentSubscriptionToStreamCompleted.UpdatePersistentSubscriptionToStreamResult
							.AccessDenied:
							code = HttpStatusCode.Unauthorized;
							break;
						case ClientMessage.UpdatePersistentSubscriptionToStreamCompleted.UpdatePersistentSubscriptionToStreamResult
							.Fail:
							code = HttpStatusCode.BadRequest;
							break;
						default:
							code = HttpStatusCode.InternalServerError;
							break;
					}

					return new ResponseConfiguration(code, http.ResponseCodec.ContentType,
						http.ResponseCodec.Encoding,
						new KeyValuePair<string, string>("location",
							MakeUrl(http, "/subscriptions/" + stream + "/" + groupname)));
				});
			http.ReadTextRequestAsync(
				(o, s) => {
					var data = http.RequestCodec.From<SubscriptionConfigData>(s);
					var config = ParseConfig(data);
					if (!ValidateConfig(config, http)) return;
					var message = new ClientMessage.UpdatePersistentSubscriptionToStream(Guid.NewGuid(),
						Guid.NewGuid(),
						envelope,
						stream,
						groupname,
						config.ResolveLinktos,
						#pragma warning disable 612
						config.StartPosition != null ? long.Parse(config.StartPosition) : config.StartFrom,
						#pragma warning restore 612
						config.MessageTimeoutMilliseconds,
						config.ExtraStatistics,
						config.MaxRetryCount,
						config.BufferSize,
						config.LiveBufferSize,
						config.ReadBatchSize,
						config.CheckPointAfterMilliseconds,
						config.MinCheckPointCount,
						config.MaxCheckPointCount,
						config.MaxSubscriberCount,
						CalculateNamedConsumerStrategyForOldClients(data),
						http.User);
					Publish(message);
				}, x => Log.Debug(x, "Reply Text Content Failed."));
		}

		private SubscriptionConfigData ParseConfig(SubscriptionConfigData config) {
			if (config == null) {
				return new SubscriptionConfigData();
			}

			return new SubscriptionConfigData {
				ResolveLinktos = config.ResolveLinktos,
				#pragma warning disable 612
				StartFrom = config.StartFrom,
				#pragma warning restore 612
				StartPosition = config.StartPosition,
				MessageTimeoutMilliseconds = config.MessageTimeoutMilliseconds,
				ExtraStatistics = config.ExtraStatistics,
				MaxRetryCount = config.MaxRetryCount,
				BufferSize = config.BufferSize,
				LiveBufferSize = config.LiveBufferSize,
				ReadBatchSize = config.ReadBatchSize,
				CheckPointAfterMilliseconds = config.CheckPointAfterMilliseconds,
				MinCheckPointCount = config.MinCheckPointCount,
				MaxCheckPointCount = config.MaxCheckPointCount,
				MaxSubscriberCount = config.MaxSubscriberCount
			};
		}

		private bool ValidateConfig(SubscriptionConfigData config, HttpEntityManager http) {
			if (config.BufferSize <= 0) {
				SendBadRequest(
					http,
					string.Format(
						"Buffer Size ({0}) must be positive",
						config.BufferSize));
				return false;
			}

			if (config.LiveBufferSize <= 0) {
				SendBadRequest(
					http,
					string.Format(
						"Live Buffer Size ({0}) must be positive",
						config.LiveBufferSize));
				return false;
			}

			if (config.ReadBatchSize <= 0) {
				SendBadRequest(
					http,
					string.Format(
						"Read Batch Size ({0}) must be positive",
						config.ReadBatchSize));
				return false;
			}

			if (!(config.BufferSize > config.ReadBatchSize)) {
				SendBadRequest(
					http,
					string.Format(
						"BufferSize ({0}) must be larger than ReadBatchSize ({1})",
						config.BufferSize, config.ReadBatchSize));
				return false;
			}

			return true;
		}

		private static string CalculateNamedConsumerStrategyForOldClients(SubscriptionConfigData data) {
			var namedConsumerStrategy = data == null ? null : data.NamedConsumerStrategy;
			if (string.IsNullOrEmpty(namedConsumerStrategy)) {
				var preferRoundRobin = data == null || data.PreferRoundRobin;
				namedConsumerStrategy = preferRoundRobin
					? SystemConsumerStrategies.RoundRobin
					: SystemConsumerStrategies.DispatchToSingle;
			}

			return namedConsumerStrategy;
		}

		private void GetAllSubscriptionInfo(HttpEntityManager http, UriTemplateMatch match) {
			if (_httpForwarder.ForwardRequest(http))
				return;

			var offsetValue = match.BoundVariables["offset"];
			var countValue = match.BoundVariables["count"];
			if (offsetValue.IsEmptyString() && countValue.IsEmptyString()) {
				GetSubscriptionInfoUnpaged(http);
				return;
			}

			if (offsetValue.IsEmptyString() || !int.TryParse(offsetValue, out var offset) || offset < 0) {
				SendBadRequest(http,
					string.Format("Offset must be a non-negative integer 'offset' ='{0}'", offsetValue));
				return;
			}

			if (countValue.IsEmptyString() || !int.TryParse(countValue, out var count) || count < 1) {
				SendBadRequest(http,
					string.Format("Count must be a positive integer 'count' ='{0}'", countValue));
				return;
			}

			var envelope = new SendToHttpEnvelope(
				_networkSendQueue, http,
				(args, message) =>
					http.ResponseCodec.To(ToPagedSummaryDto(http,
						message as MonitoringMessage.GetPersistentSubscriptionStatsCompleted)),
				(args, message) => StatsConfiguration(args, message));
			var cmd = new MonitoringMessage.GetAllPersistentSubscriptionStats(envelope, offset, count);
			Publish(cmd);
		}

		private void GetSubscriptionInfoUnpaged(HttpEntityManager http) {
			var envelope = new SendToHttpEnvelope(
				_networkSendQueue, http,
				(args, message) =>
					http.ResponseCodec.To(ToSummaryDto(http,
						message as MonitoringMessage.GetPersistentSubscriptionStatsCompleted).ToArray()),
				(args, message) => StatsConfiguration(args, message));
			var cmd = new MonitoringMessage.GetAllPersistentSubscriptionStats(envelope);
			Publish(cmd);
		}

		private static ResponseConfiguration StatsConfiguration(HttpResponseConfiguratorArgs http, Message message) {
			int code;
			if (message is ClientMessage.NotHandled notHandled)
				return Configure.HandleNotHandled(http.RequestedUrl, notHandled);

			var m = message as MonitoringMessage.GetPersistentSubscriptionStatsCompleted;
			if (m == null) throw new Exception("unexpected message " + message);
			switch (m.Result) {
				case MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.Success:
					code = HttpStatusCode.OK;
					break;
				case MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotFound:
					code = HttpStatusCode.NotFound;
					break;
				case MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotReady:
					code = HttpStatusCode.ServiceUnavailable;
					break;
				default:
					code = HttpStatusCode.InternalServerError;
					break;
			}

			return new ResponseConfiguration(code, http.ResponseCodec.ContentType,
				http.ResponseCodec.Encoding);
		}

		private IEnumerable<SubscriptionSummary> ToSummaryDto(HttpEntityManager manager,
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted message) {
			if (message == null) yield break;
			if (message.SubscriptionStats == null) yield break;

			foreach (var stat in message.SubscriptionStats) {
				var info = new SubscriptionSummary {
					Links = new List<RelLink>(),
					EventStreamId = stat.EventSource,
					GroupName = stat.GroupName,
					Status = stat.Status,
					AverageItemsPerSecond = stat.AveragePerSecond,
					TotalItemsProcessed = stat.TotalItems,
					#pragma warning disable 612
					LastKnownEventNumber = long.TryParse(stat.LastKnownEventPosition, out var lastKnownMsg) ? lastKnownMsg : 0,
					#pragma warning restore 612
					LastKnownEventPosition = stat.LastKnownEventPosition,
					#pragma warning disable 612
					LastProcessedEventNumber = long.TryParse(stat.LastCheckpointedEventPosition, out var lastEventPos) ? lastEventPos : 0,
					#pragma warning restore 612
					LastCheckpointedEventPosition = stat.LastCheckpointedEventPosition,
					TotalInFlightMessages = stat.TotalInFlightMessages,
				};
				if (stat.Connections != null) {
					info.ConnectionCount = stat.Connections.Count;
				}

				yield return info;
			}
		}

		private PagedSubscriptionInfo ToPagedSummaryDto(HttpEntityManager manager,
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted message) {
			var subscriptions = ToSummaryDto(manager, message).ToArray();
			var offset = message?.RequestedOffset ?? 0;
			var count = message?.RequestedCount ?? 0;
			var total = message?.Total ?? subscriptions.Length;

			var links = new List<RelLink> {
				new RelLink(MakeUrl(manager, "/subscriptions", CreatePagingQuery(offset, count)), "self"),
				new RelLink(MakeUrl(manager, "/subscriptions", CreatePagingQuery(0, count)), "first"),
			};

			if (offset > 0) {
				links.Add(new RelLink(
					MakeUrl(manager, "/subscriptions",
						CreatePagingQuery(Math.Max(0, offset - count), count)),
					"previous"));
			}

			if (offset + count < total) {
				links.Add(new RelLink(
					MakeUrl(manager, "/subscriptions", CreatePagingQuery(offset + count, count)),
					"next"));
			}

			return new PagedSubscriptionInfo {
				Links = links,
				Offset = offset,
				Count = count,
				Total = total,
				Subscriptions = subscriptions,
			};
		}

		private static string CreatePagingQuery(int offset, int count) =>
			string.Format(CultureInfo.InvariantCulture, "offset={0}&count={1}", offset, count);

		public class SubscriptionConfigData {
			public bool ResolveLinktos { get; set; }
			[Obsolete] public long StartFrom { get; set; }
			public string StartPosition { get; set; }
			public int MessageTimeoutMilliseconds { get; set; }
			public bool ExtraStatistics { get; set; }
			public int MaxRetryCount { get; set; }
			public int LiveBufferSize { get; set; }
			public int BufferSize { get; set; }
			public int ReadBatchSize { get; set; }
			public bool PreferRoundRobin { get; set; }
			public int CheckPointAfterMilliseconds { get; set; }
			public int MinCheckPointCount { get; set; }
			public int MaxCheckPointCount { get; set; }
			public int MaxSubscriberCount { get; set; }
			public string NamedConsumerStrategy { get; set; }

			public SubscriptionConfigData() {
				#pragma warning disable 612
				StartFrom = 0;
				#pragma warning restore 612
				StartPosition = null;
				MessageTimeoutMilliseconds = 10000;
				MaxRetryCount = 10;
				CheckPointAfterMilliseconds = 1000;
				MinCheckPointCount = 10;
				MaxCheckPointCount = 500;
				MaxSubscriberCount = 10;
				NamedConsumerStrategy = "RoundRobin";

				BufferSize = 500;
				LiveBufferSize = 500;
				ReadBatchSize = 20;
			}
		}

		public class PagedSubscriptionInfo {
			public List<RelLink> Links { get; set; }
			public int Offset { get; set; }
			public int Count { get; set; }
			public int Total { get; set; }
			public SubscriptionSummary[] Subscriptions { get; set; }
		}

		public class SubscriptionSummary {
			public List<RelLink> Links { get; set; }
			public string EventStreamId { get; set; }
			public string GroupName { get; set; }
			public string Status { get; set; }
			public decimal AverageItemsPerSecond { get; set; }
			public long TotalItemsProcessed { get; set; }
			[Obsolete] public long LastProcessedEventNumber { get; set; }
			public string LastCheckpointedEventPosition { get; set; }
			[Obsolete] public long LastKnownEventNumber { get; set; }
			public string LastKnownEventPosition { get; set; }
			public int ConnectionCount { get; set; }
			public int TotalInFlightMessages { get; set; }
		}

	}
}
