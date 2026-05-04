using System;
using System.Collections.Generic;
using EventStore.Core.Bus;
using EventStore.Plugins.Authorization;
using EventStore.Transport.Http;
using EventStore.Transport.Http.Codecs;
using EventStore.Transport.Http.EntityManagement;

namespace EventStore.Core.Services.Transport.Http.Controllers {
	public class NodeUiController : CommunicationController {
		public NodeUiController(IPublisher publisher)
			: base(publisher) {
		}

		protected override void SubscribeCore(IHttpService service) {
			RegisterRedirectAction(service, "", "/ui");
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
