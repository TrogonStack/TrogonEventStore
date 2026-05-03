using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Security.Claims;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Plugins.Authorization;
using EventStore.Transport.Http;
using EventStore.Transport.Http.Codecs;
using EventStore.Transport.Http.EntityManagement;
using Serilog;

namespace EventStore.Core.Services.Transport.Http.Controllers {
	public class AdminController : CommunicationController {
		private readonly IPublisher _networkSendQueue;
		private static readonly ILogger Log = Serilog.Log.ForContext<AdminController>();

		private static readonly ICodec[] SupportedCodecs = new ICodec[]
			{Codec.Text, Codec.Json, Codec.Xml, Codec.ApplicationXml};

		public AdminController(IPublisher publisher, IPublisher networkSendQueue) : base(publisher) {
			_networkSendQueue = networkSendQueue;
		}

		protected override void SubscribeCore(IHttpService service) {
			service.RegisterAction(
				new ControllerAction("/admin/shutdown", HttpMethod.Post, Codec.NoCodecs, SupportedCodecs, new Operation(Operations.Node.Shutdown)),
				OnPostShutdown);
			service.RegisterAction(
				new ControllerAction("/admin/reloadconfig", HttpMethod.Post, Codec.NoCodecs, SupportedCodecs, new Operation(Operations.Node.ReloadConfiguration)),
				OnPostReloadConfig);
			service.RegisterAction(
				new ControllerAction("/admin/scavenge?startFromChunk={startFromChunk}&threads={threads}&threshold={threshold}&throttlePercent={throttlePercent}&syncOnly={syncOnly}",
					HttpMethod.Post, Codec.NoCodecs, SupportedCodecs, new Operation(Operations.Node.Scavenge.Start)), OnPostScavenge);
			service.RegisterAction(
				new ControllerAction("/admin/scavenge/{scavengeId}", HttpMethod.Delete, Codec.NoCodecs,
					SupportedCodecs, new Operation(Operations.Node.Scavenge.Stop)), OnStopScavenge);
			service.RegisterAction(
				new ControllerAction("/admin/scavenge/current", HttpMethod.Get, Codec.NoCodecs, SupportedCodecs, new Operation(Operations.Node.Scavenge.Read)),
				OnGetCurrentScavenge);
			service.RegisterAction(
				new ControllerAction("/admin/scavenge/last", HttpMethod.Get, Codec.NoCodecs, SupportedCodecs, new Operation(Operations.Node.Scavenge.Read)),
				OnGetLastScavenge);
			service.RegisterAction(
				new ControllerAction("/admin/mergeindexes", HttpMethod.Post, Codec.NoCodecs, SupportedCodecs, new Operation(Operations.Node.MergeIndexes)),
				OnPostMergeIndexes);
			service.RegisterAction(
				new ControllerAction("/admin/node/priority/{nodePriority}", HttpMethod.Post, Codec.NoCodecs, SupportedCodecs, new Operation(Operations.Node.SetPriority)),
				OnSetNodePriority);
			service.RegisterAction(
				new ControllerAction("/admin/node/resign", HttpMethod.Post, Codec.NoCodecs, SupportedCodecs, new Operation(Operations.Node.Resign)),
				OnResignNode);
			service.RegisterAction(
				new ControllerAction("/admin/login", HttpMethod.Get, Codec.NoCodecs, SupportedCodecs, new Operation(Operations.Node.Login)),
				OnGetLogin);
		}

		private void OnPostShutdown(HttpEntityManager entity, UriTemplateMatch match) {
			if (entity.User != null &&
			    (entity.User.LegacyRoleCheck(SystemRoles.Admins) || entity.User.LegacyRoleCheck(SystemRoles.Operations))) {
				Log.Information("Request shut down of node because shutdown command has been received.");
				Publish(new ClientMessage.RequestShutdown(exitProcess: true, shutdownHttp: true));
				entity.ReplyStatus(HttpStatusCode.OK, "OK", LogReplyError);
			} else {
				entity.ReplyStatus(HttpStatusCode.Unauthorized, "Unauthorized", LogReplyError);
			}
		}

		private void OnPostReloadConfig(HttpEntityManager entity, UriTemplateMatch match) {
			if (entity.User != null &&
			    (entity.User.LegacyRoleCheck(SystemRoles.Admins) || entity.User.LegacyRoleCheck(SystemRoles.Operations))) {
				Log.Information("Reloading the node's configuration since a request has been received on /admin/reloadconfig.");
				Publish(new ClientMessage.ReloadConfig());
				entity.ReplyStatus(HttpStatusCode.OK, "OK", LogReplyError);
			} else {
				entity.ReplyStatus(HttpStatusCode.Unauthorized, "Unauthorized", LogReplyError);
			}
		}

		private void OnPostMergeIndexes(HttpEntityManager entity, UriTemplateMatch match) {
			Log.Information("Request merge indexes because /admin/mergeindexes request has been received.");

			var correlationId = Guid.NewGuid();
			var envelope = new SendToHttpEnvelope(_networkSendQueue, entity,
				(e, message) => { return e.ResponseCodec.To(new MergeIndexesResultDto(correlationId.ToString())); },
				(e, message) => {
					var completed = message as ClientMessage.MergeIndexesResponse;
					switch (completed?.Result) {
						case ClientMessage.MergeIndexesResponse.MergeIndexesResult.Started:
							return Configure.Ok(e.ResponseCodec.ContentType);
						default:
							return Configure.InternalServerError();
					}
				}
			);

			Publish(new ClientMessage.MergeIndexes(envelope, correlationId, entity.User));
		}

		private void OnPostScavenge(HttpEntityManager entity, UriTemplateMatch match) {
			int startFromChunk = 0;
			var startFromChunkVariable = match.BoundVariables["startFromChunk"];
			if (startFromChunkVariable != null) {
				if (!int.TryParse(startFromChunkVariable, out startFromChunk) || startFromChunk < 0) {
					SendBadRequest(entity, "startFromChunk must be a positive integer");
					return;
				}
			}

			int threads = 1;
			var threadsVariable = match.BoundVariables["threads"];
			if (threadsVariable != null) {
				if (!int.TryParse(threadsVariable, out threads) || threads < 1) {
					SendBadRequest(entity, "threads must be a 1 or above");
					return;
				}
			}

			int? threshold = null;
			var thresholdVariable = match.BoundVariables["threshold"];
			if (thresholdVariable != null) {
				if (!int.TryParse(thresholdVariable, out var x)) {
					SendBadRequest(entity, "threshold must be an integer");
					return;
				}

				threshold = x;
			}

			int? throttlePercent = null;
			var throttlePercentVariable = match.BoundVariables["throttlePercent"];
			if (throttlePercentVariable != null) {
				if (!int.TryParse(throttlePercentVariable, out var x) || x <= 0 || x > 100) {
					SendBadRequest(entity, "throttlePercent must be between 1 and 100 inclusive");
					return;
				}

				if (x != 100 && threads > 1) {
					SendBadRequest(entity, "throttlePercent must be 100 for a multi-threaded scavenge");
					return;
				}

				throttlePercent = x;
			}

			var syncOnly = false;
			var syncOnlyVariable = match.BoundVariables["syncOnly"];
			if (syncOnlyVariable != null) {
				if (!bool.TryParse(syncOnlyVariable, out var x)) {
					SendBadRequest(entity, "syncOnly must be a boolean");
					return;
				}

				syncOnly = x;
			}

			var sb = new StringBuilder();
			var args = new List<object>();

			sb.Append("Request scavenging because /admin/scavenge");
			sb.Append("?startFromChunk={chunkStartNumber}");
			args.Add(startFromChunk);
			sb.Append("&threads={numThreads}");
			args.Add(threads);

			if (threshold != null) {
				sb.Append("&threshold={threshold}");
				args.Add(threshold);
			}

			if (throttlePercent != null) {
				sb.Append("&throttlePercent={throttlePercent}");
				args.Add(throttlePercent);
			}

			sb.Append("&syncOnly={syncOnly}");
			args.Add(syncOnly);

			sb.Append(" request has been received.");
			Log.Information(sb.ToString(), args.ToArray());

			var envelope = new SendToHttpEnvelope<ClientMessage.ScavengeDatabaseStartedResponse>(_networkSendQueue, entity,(e, message) => {
					return e.To(new ScavengeResultDto(message?.ScavengeId));
				},
				(e, message) => {
					return Configure.Ok(e.ContentType);
				}, CreateErrorEnvelope(entity)
			);

			Publish(new ClientMessage.ScavengeDatabase(
				envelope: envelope,
				correlationId: Guid.Empty,
				user: entity.User,
				startFromChunk: startFromChunk,
				threads: threads,
				threshold: threshold,
				throttlePercent: throttlePercent,
				syncOnly: syncOnly));
		}

		private void OnStopScavenge(HttpEntityManager entity, UriTemplateMatch match) {
			var scavengeId = match.BoundVariables["scavengeId"];

			Log.Information("Stopping scavenge because /admin/scavenge/{scavengeId} DELETE request has been received.",
				scavengeId);

			var envelope = new SendToHttpEnvelope<ClientMessage.ScavengeDatabaseStoppedResponse>(_networkSendQueue, entity, (e, message) => {
					return e.To(message?.ScavengeId);
				},
				(e, message) => {
					return Configure.Ok(e.ContentType);
				}, CreateErrorEnvelope(entity)
			);

			Publish(new ClientMessage.StopDatabaseScavenge(envelope, Guid.Empty, entity.User, scavengeId));
		}

		private void OnGetCurrentScavenge(HttpEntityManager entity, UriTemplateMatch match) {
			Log.Information("/admin/scavenge/current GET request has been received.");

			var envelope = new SendToHttpEnvelope<ClientMessage.ScavengeDatabaseGetCurrentResponse>(
				_networkSendQueue,
				entity,
				(e, message) => {
					var result = new ScavengeGetCurrentResultDto();

					if (message is not null &&
					    message.Result == ClientMessage.ScavengeDatabaseGetCurrentResponse.ScavengeResult.InProgress &&
					    message.ScavengeId is not null) {

						result.ScavengeId = message.ScavengeId;
						result.ScavengeLink = $"/admin/scavenge/{message.ScavengeId}";
					}

					return e.To(result);
				},
				(e, message) => {
					return Configure.Ok(e.ContentType);
				}, CreateErrorEnvelope(entity)
			);

			Publish(new ClientMessage.GetCurrentDatabaseScavenge(envelope, Guid.Empty, entity.User));
		}

		private void OnGetLastScavenge(HttpEntityManager entity, UriTemplateMatch match) {
			Log.Information("/admin/scavenge/last GET request has been received.");

			var envelope = new SendToHttpEnvelope<ClientMessage.ScavengeDatabaseGetLastResponse>(
				_networkSendQueue,
				entity,
				(e, message) => {
					var result = new ScavengeGetLastResultDto();
					if (message.ScavengeId is not null) {
						result.ScavengeId = message.ScavengeId;
						result.ScavengeLink = $"/admin/scavenge/{message.ScavengeId}";
					}
					result.ScavengeResult = message.Result.ToString();

					return e.To(result);
				},
				(e, message) => {
					return Configure.Ok(e.ContentType);
				}, CreateErrorEnvelope(entity)
			);

			Publish(new ClientMessage.GetLastDatabaseScavenge(envelope, Guid.Empty, entity.User));
		}

		private void OnSetNodePriority(HttpEntityManager entity, UriTemplateMatch match) {
			if (entity.User != null &&
			    (entity.User.LegacyRoleCheck(SystemRoles.Admins) || entity.User.LegacyRoleCheck(SystemRoles.Operations))) {
				Log.Information("Request to set node priority.");

				int nodePriority;
				var nodePriorityVariable = match.BoundVariables["nodePriority"];
				if (nodePriorityVariable == null) {
					SendBadRequest(entity, "Could not find expected `nodePriority` in the request body.");
					return;
				}

				if (!int.TryParse(nodePriorityVariable, out nodePriority)) {
					SendBadRequest(entity, "nodePriority must be an integer.");
					return;
				}

				Publish(new ClientMessage.SetNodePriority(nodePriority));
				entity.ReplyStatus(HttpStatusCode.OK, "OK", LogReplyError);
			} else {
				entity.ReplyStatus(HttpStatusCode.Unauthorized, "Unauthorized", LogReplyError);
			}
		}

		private void OnResignNode(HttpEntityManager entity, UriTemplateMatch match) {
			if (entity.User != null &&
			    (entity.User.LegacyRoleCheck(SystemRoles.Admins) || entity.User.LegacyRoleCheck(SystemRoles.Operations))) {
				Log.Information("Request to resign node.");
				Publish(new ClientMessage.ResignNode());
				entity.ReplyStatus(HttpStatusCode.OK, "OK", LogReplyError);
			} else {
				entity.ReplyStatus(HttpStatusCode.Unauthorized, "Unauthorized", LogReplyError);
			}
		}

		private void OnGetLogin(HttpEntityManager entity, UriTemplateMatch match) {
			var message = new UserManagementMessage.UserDetailsResult(
				new UserManagementMessage.UserData(
					entity.User.Identity.Name,
					entity.User.Identity.Name,
					entity.User.Claims.Where(x => x.Type == ClaimTypes.Role).Select(x => x.Value).ToArray(),
					false,
					new DateTimeOffset(DateTime.UtcNow)));

			entity.ReplyTextContent(
				message.ToJson(),
				HttpStatusCode.OK,
				"",
				ContentType.Json,
				new List<KeyValuePair<string, string>>(),
				e => Log.Error(e, "Error while writing HTTP response"));
		}

		private IEnvelope CreateErrorEnvelope(HttpEntityManager http) {
			return new SendToHttpEnvelope<ClientMessage.ScavengeDatabaseInProgressResponse>(
				_networkSendQueue,
				http,
				ScavengeInProgressFormatter,
				ScavengeInProgressConfigurator,
				new SendToHttpEnvelope<ClientMessage.ScavengeDatabaseNotFoundResponse>(
						_networkSendQueue,
						http,
						ScavengeNotFoundFormatter,
						ScavengeNotFoundConfigurator,
						new SendToHttpEnvelope<ClientMessage.ScavengeDatabaseUnauthorizedResponse>(
							_networkSendQueue,
							http,
							ScavengeUnauthorizedFormatter,
							ScavengeUnauthorizedConfigurator,
							null)));
		}

		private ResponseConfiguration ScavengeInProgressConfigurator(ICodec codec, ClientMessage.ScavengeDatabaseInProgressResponse message) {
			return new ResponseConfiguration(HttpStatusCode.BadRequest, "Bad Request", "text/plain", Helper.UTF8NoBom);
		}

		private string ScavengeInProgressFormatter(ICodec codec, ClientMessage.ScavengeDatabaseInProgressResponse message) {
			return message.Reason;
		}

		private ResponseConfiguration ScavengeNotFoundConfigurator(ICodec codec, ClientMessage.ScavengeDatabaseNotFoundResponse message) {
			return new ResponseConfiguration(HttpStatusCode.NotFound, "Not Found", "text/plain", Helper.UTF8NoBom);
		}

		private string ScavengeNotFoundFormatter(ICodec codec, ClientMessage.ScavengeDatabaseNotFoundResponse message) {
			return message.Reason;
		}

		private ResponseConfiguration ScavengeUnauthorizedConfigurator(ICodec codec, ClientMessage.ScavengeDatabaseUnauthorizedResponse message) {
			return new ResponseConfiguration(HttpStatusCode.Unauthorized, "Unauthorized", "text/plain", Helper.UTF8NoBom);
		}

		private string ScavengeUnauthorizedFormatter(ICodec codec, ClientMessage.ScavengeDatabaseUnauthorizedResponse message) {
			return message.Reason;
		}

		private void LogReplyError(Exception exc) {
			Log.Debug("Error while closing HTTP connection (admin controller): {e}.", exc.Message);
		}
	}
}
