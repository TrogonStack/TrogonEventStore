using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Plugins.Authorization;
using EventStore.Transport.Http;
using EventStore.Transport.Http.Codecs;
using EventStore.Transport.Http.EntityManagement;
using Serilog;

namespace EventStore.Core.Services.Transport.Http.Controllers {
	public class AdminController : CommunicationController {
		private static readonly ILogger Log = Serilog.Log.ForContext<AdminController>();

		private static readonly ICodec[] SupportedCodecs = new ICodec[]
			{Codec.Text, Codec.Json, Codec.Xml, Codec.ApplicationXml};

		public AdminController(IPublisher publisher) : base(publisher) {
		}

		protected override void SubscribeCore(IHttpService service) {
			service.RegisterAction(
				new ControllerAction("/admin/login", HttpMethod.Get, Codec.NoCodecs, SupportedCodecs, new Operation(Operations.Node.Login)),
				OnGetLogin);
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

		private void LogReplyError(Exception exc) {
			Log.Debug("Error while closing HTTP connection (admin controller): {e}.", exc.Message);
		}
	}
}
