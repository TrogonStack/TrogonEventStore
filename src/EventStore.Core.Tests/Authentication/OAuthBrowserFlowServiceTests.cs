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
		var service = new OAuthBrowserFlowService(Options(), new HttpClient(new TokenHandler()), TimeProvider.System);

		var challenge = service.CreateCodeChallenge();
		var json = JsonSerializer.Serialize(challenge, OAuthBrowserFlowService.JsonOptions);

		Assert.That(json, Does.Contain("code_challenge_correlation_id"));
		Assert.That(json, Does.Contain("code_challenge"));
		Assert.That(json, Does.Contain("code_challenge_method"));
		Assert.AreEqual("S256", challenge.CodeChallengeMethod);
		Assert.That(challenge.CodeChallenge, Is.Not.Empty);
		Assert.That(challenge.CodeChallengeCorrelationId, Is.Not.Empty);
	}

	[Test]
	public async Task callback_exchanges_code_and_sets_oauth_token_cookie()
	{
		var handler = new TokenHandler();
		var service = new OAuthBrowserFlowService(Options(), new HttpClient(handler), TimeProvider.System);
		var challenge = service.CreateCodeChallenge();
		var context = new DefaultHttpContext();
		context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
		context.Request.Scheme = "https";
		context.Request.Host = new HostString("node.example.test");
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			["code"] = new StringValues("authorization-code"),
			["state"] = new StringValues(State(challenge.CodeChallengeCorrelationId))
		});

		var result = await service.HandleCallback(context, CancellationToken.None);
		await result.ExecuteAsync(context);

		Assert.AreEqual(HttpStatusCode.Redirect, (HttpStatusCode)context.Response.StatusCode);
		Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/ui/signin"));
		Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Contain($"{UiCredentialCookie.OAuthCookieName}=access-token"));
		Assert.That(handler.Body, Does.Contain("grant_type=authorization_code"));
		Assert.That(handler.Body, Does.Contain("client_id=eventstore-ui"));
		Assert.That(handler.Body, Does.Contain("redirect_uri=https%3A%2F%2Fnode.example.test%2Fui%2Fauth%2Foauth%2Fcallback"));
		Assert.That(handler.Body, Does.Contain("code_verifier="));
	}

	private static ClusterVNodeOptions.OAuthOptions Options() => new()
	{
		Issuer = "https://login.example.test",
		Audiences = ["eventstore"],
		TokenEndpoint = "https://login.example.test/token",
		ClientId = "eventstore-ui"
	};

	private static string State(string correlationId) =>
		Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"code_challenge_correlation_id":"{{correlationId}}"}"""));

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
