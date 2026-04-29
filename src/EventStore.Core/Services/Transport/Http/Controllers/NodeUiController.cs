using System;
using System.Collections.Generic;
using EventStore.Core.Bus;
using EventStore.Plugins.Authorization;
using EventStore.Transport.Http;
using EventStore.Transport.Http.Codecs;
using EventStore.Transport.Http.EntityManagement;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.Services.Transport.Http.Controllers {
	public class NodeUiController : CommunicationController {
		private static readonly ILogger Log = Serilog.Log.ForContext<NodeUiController>();

		private readonly NodeSubsystems[] _enabledNodeSubsystems;

		public NodeUiController(IPublisher publisher, NodeSubsystems[] enabledNodeSubsystems)
			: base(publisher) {
			_enabledNodeSubsystems = enabledNodeSubsystems;
		}

		protected override void SubscribeCore(IHttpService service) {
			RegisterRedirectAction(service, "", "/ui");

			service.RegisterAction(
				new ControllerAction("/sys/subsystems", HttpMethod.Get, Codec.NoCodecs, new ICodec[] {Codec.Json}, new Operation(Operations.Node.Information.Subsystems)),
				OnListNodeSubsystems);
		}

		private void OnListNodeSubsystems(HttpEntityManager http, UriTemplateMatch match) {
			http.ReplyTextContent(
				Codec.Json.To(_enabledNodeSubsystems),
				200,
				"OK",
				"application/json",
				null,
				ex => Log.Information(ex, "Failed to prepare main menu")
			);
		}

		private static void RegisterRedirectAction(IHttpService service, string fromUrl, string toUrl) {
			service.RegisterAction(
				new ControllerAction(
					fromUrl,
					HttpMethod.Get,
					Codec.NoCodecs,
					new ICodec[] {Codec.ManualEncoding},
					new Operation(Operations.Node.Redirect)),
				(http, match) => http.ReplyTextContent(
					"Moved", 302, "Found", "text/plain",
					new[] {
						new KeyValuePair<string, string>(
							"Location", new Uri(http.HttpEntity.RequestedUrl, toUrl).AbsoluteUri)
					}, Console.WriteLine));
		}
	}
}
