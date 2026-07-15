using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventStore.ClusterNode.Components.Services;
using EventStore.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using NUnit.Framework;

namespace EventStore.Core.Tests.Authentication;

[TestFixture]
public class OAuthBrowserFlowServiceTests
{
	[Test]
	public void creates_code_challenge_using_browser_contract_names()
	{
		var service = Service(new TokenHandler());
		var context = HttpsContext();

		var challenge = service.CreateCodeChallenge(context);
		var json = JsonSerializer.Serialize(challenge, OAuthBrowserFlowService.JsonOptions);

		Assert.That(json, Does.Contain("code_challenge_correlation_id"));
		Assert.That(json, Does.Contain("code_challenge"));
		Assert.That(json, Does.Contain("code_challenge_method"));
		Assert.AreEqual("S256", challenge.CodeChallengeMethod);
		Assert.That(challenge.CodeChallenge, Is.Not.Empty);
		Assert.That(challenge.CodeChallengeCorrelationId, Is.Not.Empty);
		Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Contain("eventstore-ui-oauth-pkce="));
	}

	[Test]
	public async Task callback_exchanges_code_and_sets_oauth_token_cookie()
	{
		var handler = new TokenHandler();
		var service = Service(handler);
		var challengeContext = HttpsContext();
		var challenge = service.CreateCodeChallenge(challengeContext);
		var context = HttpsContext();
		context.Request.Headers.Cookie = challengeContext.Response.Headers.SetCookie.ToString().Split(';')[0];
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			["code"] = new StringValues("authorization-code"),
			["state"] = new StringValues(State(challenge.CodeChallengeCorrelationId))
		});

		var result = await service.HandleCallback(context, CancellationToken.None);
		await result.ExecuteAsync(context);

		Assert.AreEqual(HttpStatusCode.Redirect, (HttpStatusCode)context.Response.StatusCode);
		Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/ui/signin?returnUrl=%2Fui%2Fstreams"));
		Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Contain($"{UiCredentialCookie.OAuthCookieName}=access-token"));
		Assert.That(handler.Body, Does.Contain("grant_type=authorization_code"));
		Assert.That(handler.Body, Does.Contain("client_id=eventstore-ui"));
		Assert.That(handler.Body, Does.Contain("redirect_uri=https%3A%2F%2Fnode.example.test%2Fui%2Fauth%2Foauth%2Fcallback"));
		Assert.That(handler.Body, Does.Contain("code_verifier="));
	}

	[Test]
	public async Task callback_redirects_to_return_url_when_admin_ui_is_disabled()
	{
		var handler = new TokenHandler();
		var service = Service(handler, adminUiEnabled: false);
		var challengeContext = HttpsContext();
		var challenge = service.CreateCodeChallenge(challengeContext);
		var context = HttpsContext();
		context.Request.Headers.Cookie = challengeContext.Response.Headers.SetCookie.ToString().Split(';')[0];
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			["code"] = new StringValues("authorization-code"),
			["state"] = new StringValues(State(challenge.CodeChallengeCorrelationId))
		});

		var result = await service.HandleCallback(context, CancellationToken.None);
		await result.ExecuteAsync(context);

		Assert.AreEqual(HttpStatusCode.Redirect, (HttpStatusCode)context.Response.StatusCode);
		Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/ui/streams"));
		Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Contain($"{UiCredentialCookie.OAuthCookieName}=access-token"));
	}

	[Test]
	public async Task callback_without_matching_challenge_cookie_does_not_exchange_code()
	{
		var handler = new TokenHandler();
		var service = Service(handler);
		var context = HttpsContext();
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			["code"] = new StringValues("authorization-code"),
			["state"] = new StringValues(State("not-this-browser"))
		});

		var result = await service.HandleCallback(context, CancellationToken.None);
		await result.ExecuteAsync(context);

		Assert.AreEqual(HttpStatusCode.Redirect, (HttpStatusCode)context.Response.StatusCode);
		Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/ui/signin?returnUrl=%2Fui%2Fstreams&oauth_error=invalid_state"));
		Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Not.Contain($"{UiCredentialCookie.OAuthCookieName}=access-token"));
		Assert.That(handler.Body, Is.Empty);
	}

	[Test]
	public async Task callback_with_provider_error_preserves_return_url_without_exchanging_code()
	{
		var handler = new TokenHandler();
		var service = Service(handler);
		var challengeContext = HttpsContext();
		var challenge = service.CreateCodeChallenge(challengeContext);
		var context = HttpsContext();
		context.Request.Headers.Cookie = challengeContext.Response.Headers.SetCookie.ToString().Split(';')[0];
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			["error"] = new StringValues("access_denied"),
			["state"] = new StringValues(State(challenge.CodeChallengeCorrelationId))
		});

		var result = await service.HandleCallback(context, CancellationToken.None);
		await result.ExecuteAsync(context);

		Assert.AreEqual(HttpStatusCode.Redirect, (HttpStatusCode)context.Response.StatusCode);
		Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/ui/signin?returnUrl=%2Fui%2Fstreams&oauth_error=provider_error"));
		Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Not.Contain($"{UiCredentialCookie.OAuthCookieName}=access-token"));
		Assert.That(handler.Body, Is.Empty);
	}

	[Test]
	public async Task callback_with_provider_error_redirects_to_return_url_when_admin_ui_is_disabled()
	{
		var handler = new TokenHandler();
		var service = Service(handler, adminUiEnabled: false);
		var challengeContext = HttpsContext();
		var challenge = service.CreateCodeChallenge(challengeContext);
		var context = HttpsContext();
		context.Request.Headers.Cookie = challengeContext.Response.Headers.SetCookie.ToString().Split(';')[0];
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			["error"] = new StringValues("access_denied"),
			["state"] = new StringValues(State(challenge.CodeChallengeCorrelationId))
		});

		var result = await service.HandleCallback(context, CancellationToken.None);
		await result.ExecuteAsync(context);

		Assert.AreEqual(HttpStatusCode.Redirect, (HttpStatusCode)context.Response.StatusCode);
		Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/ui/streams?oauth_error=provider_error"));
		Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Not.Contain($"{UiCredentialCookie.OAuthCookieName}=access-token"));
		Assert.That(handler.Body, Is.Empty);
	}

	[Test]
	public async Task callback_deletes_pkce_cookie_when_callback_data_is_missing()
	{
		var handler = new TokenHandler();
		var service = Service(handler);
		var challengeContext = HttpsContext();
		service.CreateCodeChallenge(challengeContext);
		var context = HttpsContext();
		context.Request.Headers.Cookie = challengeContext.Response.Headers.SetCookie.ToString().Split(';')[0];

		var result = await service.HandleCallback(context, CancellationToken.None);
		await result.ExecuteAsync(context);

		Assert.AreEqual(HttpStatusCode.Redirect, (HttpStatusCode)context.Response.StatusCode);
		Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/ui/signin?oauth_error=missing_callback"));
		var setCookie = context.Response.Headers.SetCookie.ToString();
		Assert.That(setCookie, Does.Contain("eventstore-ui-oauth-pkce=;"));
		Assert.That(setCookie, Does.Contain("path=/"));
		Assert.That(setCookie, Does.Contain("secure"));
		Assert.That(setCookie, Does.Contain("samesite=lax"));
		Assert.That(setCookie, Does.Contain("httponly"));
		Assert.That(handler.Body, Is.Empty);
	}

	[Test]
	public async Task callback_exchanges_code_with_redirect_uri_from_state()
	{
		var handler = new TokenHandler();
		var service = Service(handler);
		var challengeContext = HttpsContext();
		var challenge = service.CreateCodeChallenge(challengeContext);
		var context = HttpsContext();
		context.Request.Scheme = "http";
		context.Request.Host = new HostString("internal-node:2113");
		context.Request.Headers.Cookie = challengeContext.Response.Headers.SetCookie.ToString().Split(';')[0];
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			["code"] = new StringValues("authorization-code"),
			["state"] = new StringValues(State(challenge.CodeChallengeCorrelationId, "https://public.example.test/ui/auth/oauth/callback"))
		});

		var result = await service.HandleCallback(context, CancellationToken.None);
		await result.ExecuteAsync(context);

		Assert.AreEqual(HttpStatusCode.Redirect, (HttpStatusCode)context.Response.StatusCode);
		Assert.That(handler.Body, Does.Contain("redirect_uri=https%3A%2F%2Fpublic.example.test%2Fui%2Fauth%2Foauth%2Fcallback"));
		Assert.That(handler.Body, Does.Not.Contain("redirect_uri=http%3A%2F%2Finternal-node%3A2113"));
	}

	private static ClusterVNodeOptions.OAuthOptions Options() => new()
	{
		Issuer = "https://login.example.test",
		Audiences = ["eventstore"],
		TokenEndpoint = "https://login.example.test/token",
		ClientId = "eventstore-ui"
	};

	private static string State(string correlationId, string redirectUri = "https://node.example.test/ui/auth/oauth/callback") =>
		Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"code_challenge_correlation_id":"{{correlationId}}","return_url":"/ui/streams","redirect_uri":"{{redirectUri}}"}"""));

	private static OAuthBrowserFlowService Service(TokenHandler handler, bool adminUiEnabled = true)
	{
		var services = new ServiceCollection()
			.AddLogging()
			.AddDataProtection()
			.Services
			.BuildServiceProvider();
		return new OAuthBrowserFlowService(
			Options(),
			new HttpClient(handler),
			TimeProvider.System,
			services.GetRequiredService<IDataProtectionProvider>(),
			adminUiEnabled);
	}

	private static DefaultHttpContext HttpsContext()
	{
		var context = new DefaultHttpContext
		{
			RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
		};
		context.Request.Scheme = "https";
		context.Request.Host = new HostString("node.example.test");
		return context;
	}

	private sealed class TokenHandler : HttpMessageHandler
	{
		public string Body { get; private set; } = "";

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Body = await request.Content.ReadAsStringAsync(cancellationToken);
			return new(HttpStatusCode.OK)
			{
				Content = new StringContent("""{"access_token":"access-token"}""", Encoding.UTF8, "application/json")
			};
		}
	}
}
