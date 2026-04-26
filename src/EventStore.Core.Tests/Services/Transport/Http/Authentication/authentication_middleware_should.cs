using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using EventStore.Core.Services.Transport.Http;
using EventStore.Core.Services.Transport.Http.Authentication;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Http.Authentication;

[TestFixture]
public class authentication_middleware_should {
	[Test]
	public async Task preserve_anonymous_browser_redirects_regardless_of_path() {
		var context = await InvokeWithRedirect(
			new AnonymousHttpAuthenticationProvider(),
			path: "/streams/test",
			userAgent: "Mozilla/5.0");

		Assert.AreEqual(StatusCodes.Status302Found, context.Response.StatusCode);
		Assert.AreEqual("/login", context.Response.Headers.Location);
	}

	[Test]
	public async Task rewrite_anonymous_non_browser_redirects_to_unauthorized() {
		var context = await InvokeWithRedirect(
			new AnonymousHttpAuthenticationProvider(),
			path: "/web/index.html",
			userAgent: "curl/8.0");

		Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
		Assert.False(context.Response.Headers.ContainsKey(HeaderNames.Location));
		Assert.AreEqual("X-Basic realm=\"ESDB\"", context.Response.Headers.WWWAuthenticate);
	}

	[Test]
	public async Task rewrite_anonymous_redirects_without_a_user_agent_to_unauthorized() {
		var context = await InvokeWithRedirect(
			new AnonymousHttpAuthenticationProvider(),
			path: "/ui");

		Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
		Assert.False(context.Response.Headers.ContainsKey(HeaderNames.Location));
		Assert.AreEqual("X-Basic realm=\"ESDB\"", context.Response.Headers.WWWAuthenticate);
	}

	[Test]
	public async Task preserve_authenticated_redirects_for_non_browser_requests() {
		var context = await InvokeWithRedirect(
			new PrincipalHttpAuthenticationProvider(new ClaimsPrincipal(
				new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))),
			path: "/admin",
			userAgent: "curl/8.0");

		Assert.AreEqual(StatusCodes.Status302Found, context.Response.StatusCode);
		Assert.AreEqual("/login", context.Response.Headers.Location);
	}

	private static async Task<HttpContext> InvokeWithRedirect(
		IHttpAuthenticationProvider httpAuthenticationProvider,
		string path,
		string userAgent = null) {
		var context = new DefaultHttpContext();
		context.Request.Path = path;
		if (userAgent is not null)
			context.Request.Headers.UserAgent = userAgent;

		var middleware = new AuthenticationMiddleware(
			[httpAuthenticationProvider],
			new TestAuthenticationProvider());

		await middleware.InvokeAsync(context, ctx => {
			ctx.Response.StatusCode = StatusCodes.Status302Found;
			ctx.Response.Headers.Location = "/login";
			return Task.CompletedTask;
		});

		return context;
	}

	private sealed class PrincipalHttpAuthenticationProvider(ClaimsPrincipal principal) : IHttpAuthenticationProvider {
		public string Name => "test";

		public bool Authenticate(HttpContext context, out HttpAuthenticationRequest request) {
			request = new HttpAuthenticationRequest(context, null, null);
			request.Authenticated(principal);
			return true;
		}
	}

	private sealed class TestAuthenticationProvider : AuthenticationProviderBase {
		public override void Authenticate(AuthenticationRequest authenticationRequest) =>
			throw new System.NotImplementedException();

		public override IReadOnlyList<string> GetSupportedAuthenticationSchemes() => ["Basic"];
	}
}
