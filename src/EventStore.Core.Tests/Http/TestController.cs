using System;
using System.Security.Claims;
using System.Text;
using EventStore.Common.Utils;
using EventStore.Core.Services.Transport.Http;
using EventStore.Plugins.Authorization;
using EventStore.Transport.Http;
using EventStore.Transport.Http.Codecs;
using EventStore.Transport.Http.EntityManagement;

namespace EventStore.Core.Tests.Http;

public static class TestController {
	public static void Register(IHttpService service) {
		Register(service, "/test1", Test1Handler);
		Register(service, "/test-anonymous", TestAnonymousHandler);
		Register(service, "/test-encoding/{a}?b={b}", TestEncodingHandler);
		Register(service, "/test-encoding-reserved-%20?b={b}",
			(manager, match) => TestEncodingHandler(manager, match, "%20"));
		Register(service, "/test-encoding-reserved-%24?b={b}",
			(manager, match) => TestEncodingHandler(manager, match, "%24"));
		Register(service, "/test-encoding-reserved-%25?b={b}",
			(manager, match) => TestEncodingHandler(manager, match, "%25"));
		Register(service, "/test-encoding-reserved- ?b={b}",
			(manager, match) => TestEncodingHandler(manager, match, " "));
		Register(service, "/test-encoding-reserved-$?b={b}",
			(manager, match) => TestEncodingHandler(manager, match, "$"));
		Register(service, "/test-encoding-reserved-%?b={b}",
			(manager, match) => TestEncodingHandler(manager, match, "%"));
		Register(service, "/test-timeout?sleepfor={sleepfor}",
			(manager, match) => TestTimeoutHandler(manager, match));
	}

	private static void Register(
		IHttpService service,
		string uriTemplate,
		Action<HttpEntityManager, UriTemplateMatch> handler,
		string httpMethod = HttpMethod.Get) {
		service.RegisterAction(
			new ControllerAction(
				uriTemplate,
				httpMethod,
				Codec.NoCodecs,
				[Codec.ManualEncoding],
				new Operation(Operations.Node.StaticContent)),
			handler);
	}

	private static void Test1Handler(HttpEntityManager http, UriTemplateMatch match) {
		if (http.User != null && !http.User.HasClaim(ClaimTypes.Anonymous, ""))
			Reply(http, "OK", 200, "OK", "text/plain");
		else
			Reply(http, "Please authenticate yourself", 401, "Unauthorized", "text/plain");
	}

	private static void TestAnonymousHandler(HttpEntityManager http, UriTemplateMatch match) {
		if (!http.User.HasClaim(ClaimTypes.Anonymous, ""))
			Reply(http, "ERROR", 500, "ERROR", "text/plain");
		else
			Reply(http, "OK", 200, "OK", "text/plain");
	}

	private static void TestEncodingHandler(HttpEntityManager http, UriTemplateMatch match) {
		var a = match.BoundVariables["a"];
		var b = match.BoundVariables["b"];

		Reply(http, new { a, b, rawSegment = http.RequestedUrl.Segments[2] }.ToJson(), 200, "OK", "application/json");
	}

	private static void TestEncodingHandler(HttpEntityManager http, UriTemplateMatch match, string a) {
		var b = match.BoundVariables["b"];

		Reply(
			http,
			new {
				a,
				b,
				rawSegment = http.RequestedUrl.Segments[1],
				requestUri = match.RequestUri,
				rawUrl = http.HttpEntity.Request.RawUrl
			}.ToJson(),
			200,
			"OK",
			"application/json");
	}

	private static void TestTimeoutHandler(HttpEntityManager http, UriTemplateMatch match) {
		var sleepFor = int.Parse(match.BoundVariables["sleepfor"]);
		System.Threading.Thread.Sleep(sleepFor);
		Reply(http, "OK", 200, "OK", "text/plain");
	}

	private static void Reply(HttpEntityManager http, string response, int code, string description, string contentType) =>
		http.Reply(Helper.UTF8NoBom.GetBytes(response), code, description, contentType, Encoding.UTF8, null, _ => { });
}
