using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Http;
using EventStore.Projections.Core.Services.Http;
using EventStore.Transport.Http;
using EventStore.Transport.Http.Codecs;
using EventStore.Transport.Http.EntityManagement;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EventStore.Projections.Core.Tests.Services.Transport.Http;

[TestFixture]
public class projections_ui_redirects {
	[Test]
	public async Task projections_target_the_razor_projections_page() {
		var service = new TestHttpService();
		new ProjectionsController(new NoForwarder(), new NoopPublisher(), new NoopPublisher()).Subscribe(service);

		const string path = "/projections";
		var requestUri = new Uri($"http://127.0.0.1:2113{path}");
		var match = service.GetAllUriMatches(requestUri)
			.Single(x => x.ControllerAction.UriTemplate == path && x.ControllerAction.HttpMethod == HttpMethod.Get);
		var completed = new TaskCompletionSource<object>();
		var context = new DefaultHttpContext();
		context.Request.Scheme = "http";
		context.Request.Host = new HostString("127.0.0.1", 2113);
		context.Request.Path = path;
		context.Response.Body = new MemoryStream();

		var entity = new HttpEntity(context, false, null, 0, () => completed.SetResult(null));
		var manager = entity.CreateManager(Codec.NoCodec, Codec.ManualEncoding, new[] { HttpMethod.Get }, _ => { });
		match.RequestHandler(manager, match.TemplateMatch);

		await completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
		Assert.AreEqual(302, context.Response.StatusCode);
		Assert.AreEqual("http://127.0.0.1:2113/ui/projections", context.Response.Headers.Location.ToString());
	}

	[Test]
	public void legacy_web_projections_route_is_not_registered() {
		var service = new TestHttpService();
		new ProjectionsController(new NoForwarder(), new NoopPublisher(), new NoopPublisher()).Subscribe(service);

		var matches = service.GetAllUriMatches(new Uri("http://127.0.0.1:2113/web/projections"));

		Assert.IsEmpty(matches);
	}

	private sealed class TestHttpService : IHttpService {
		private readonly TrieUriRouter _router = new();

		public ServiceAccessibility Accessibility => ServiceAccessibility.Public;
		public bool IsListening => true;
		public IEnumerable<System.Net.EndPoint> EndPoints => Array.Empty<System.Net.EndPoint>();
		public IEnumerable<ControllerAction> Actions => _router.Actions;

		public List<UriToActionMatch> GetAllUriMatches(Uri uri) => _router.GetAllUriMatches(uri);

		public void SetupController(IHttpController controller) => controller.Subscribe(this);

		public void RegisterCustomAction(ControllerAction action, Func<HttpEntityManager, UriTemplateMatch, RequestParams> handler) =>
			_router.RegisterAction(action, handler);

		public void RegisterAction(ControllerAction action, Action<HttpEntityManager, UriTemplateMatch> handler) =>
			_router.RegisterAction(action, (manager, match) => {
				handler(manager, match);
				return new RequestParams(done: true);
			});

		public void Shutdown() { }
	}

	private sealed class NoForwarder : IHttpForwarder {
		public bool ForwardRequest(HttpEntityManager manager) => false;
	}

	private sealed class NoopPublisher : IPublisher {
		public void Publish(Message message) { }
	}
}
