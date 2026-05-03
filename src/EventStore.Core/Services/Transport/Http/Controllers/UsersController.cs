using System;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Plugins.Authorization;
using EventStore.Transport.Http;
using EventStore.Transport.Http.Codecs;
using EventStore.Transport.Http.EntityManagement;

namespace EventStore.Core.Services.Transport.Http.Controllers {
	public class UsersController : CommunicationController {
		private readonly IHttpForwarder _httpForwarder;
		private readonly IPublisher _networkSendQueue;

		public UsersController(IHttpForwarder httpForwarder, IPublisher publisher, IPublisher networkSendQueue)
			: base(publisher) {
			_httpForwarder = httpForwarder;
			_networkSendQueue = networkSendQueue;
		}

		protected override void SubscribeCore(IHttpService service) {
			RegisterUrlBased(service, "/users/$current", HttpMethod.Get, new Operation(Operations.Users.CurrentUser), GetCurrentUser);
		}

		private void GetCurrentUser(HttpEntityManager http, UriTemplateMatch match) {
			if (_httpForwarder.ForwardRequest(http))
				return;
			var envelope = CreateReplyEnvelope<UserManagementMessage.UserDetailsResult>(http);
			if (http.User == null) {
				envelope.ReplyWith(
					new UserManagementMessage.UserDetailsResult(UserManagementMessage.Error.Unauthorized));
				return;
			}

			var message = new UserManagementMessage.Get(envelope, http.User, http.User.Identity.Name);
			Publish(message);
		}

		private SendToHttpEnvelope<T> CreateReplyEnvelope<T>(
			HttpEntityManager http, Func<ICodec, T, string> formatter = null,
			Func<ICodec, T, ResponseConfiguration> configurator = null)
			where T : UserManagementMessage.ResponseMessage {
			return new SendToHttpEnvelope<T>(
				_networkSendQueue, http, formatter ?? AutoFormatter, configurator ?? AutoConfigurator, null);
		}

		private ResponseConfiguration AutoConfigurator<T>(ICodec codec, T result)
			where T : UserManagementMessage.ResponseMessage {
			return result.Success
				? new ResponseConfiguration(HttpStatusCode.OK, codec.ContentType, codec.Encoding)
				: new ResponseConfiguration(
					ErrorToHttpStatusCode(result.Error), codec.ContentType, codec.Encoding);
		}

		private string AutoFormatter<T>(ICodec codec, T result) {
			return codec.To(result);
		}

		private int ErrorToHttpStatusCode(UserManagementMessage.Error error) {
			switch (error) {
				case UserManagementMessage.Error.Success:
					return HttpStatusCode.OK;
				case UserManagementMessage.Error.Conflict:
					return HttpStatusCode.Conflict;
				case UserManagementMessage.Error.NotFound:
					return HttpStatusCode.NotFound;
				case UserManagementMessage.Error.Error:
					return HttpStatusCode.InternalServerError;
				case UserManagementMessage.Error.TryAgain:
					return HttpStatusCode.RequestTimeout;
				case UserManagementMessage.Error.Unauthorized:
					return HttpStatusCode.Unauthorized;
				default:
					return HttpStatusCode.InternalServerError;
			}
		}

	}
}
