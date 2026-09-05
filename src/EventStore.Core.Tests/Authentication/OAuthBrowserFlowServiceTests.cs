using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventStore.ClusterNode.Components.Services;
using EventStore.Core;
using EventStore.Core.Authentication.OAuth;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EventStore.Core.Tests.Authentication;

[TestFixture]
public class OAuthBrowserFlowServiceTests
{
	private static readonly SymmetricSecurityKey SigningKey =
		new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToByteArray().Concat(
			Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray()).ToArray());
	private static readonly string JwtToken = CreateToken(audience: "eventstore");
	private ServiceProvider _services;
	private ControlledAuthenticationProvider _authenticationProvider;
	private readonly List<IServiceScope> _requestScopes = [];

	[SetUp]
	public void SetUp()
	{
		var services = new ServiceCollection().AddLogging();
		services.AddDataProtection().UseEphemeralDataProtectionProvider();
		_authenticationProvider = new ControlledAuthenticationProvider(new OAuthAuthenticationProvider(
			Options(), false, _ => new ValueTask<TokenValidationParameters>(CreateValidationParameters())));
		services.AddSingleton<IAuthenticationProvider>(_authenticationProvider);
		services.AddUiSessionAuthentication();
		_services = services.BuildServiceProvider();
	}

	[TearDown]
	public void TearDown()
	{
		foreach (var scope in _requestScopes)
			scope.Dispose();
		_requestScopes.Clear();
		_services.Dispose();
	}

	[Test]
	public async Task callback_authentication_timeout_redirects_without_issuing_session()
	{
		using var service = Service(new TokenHandler());
		var context = CallbackContext(service);
		_authenticationProvider.CompleteRequests = false;

		var result = await service.HandleCallback(context, CancellationToken.None);
		await result.ExecuteAsync(context);

		Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
		Assert.That(context.Response.Headers.Location.ToString(), Does.Contain("oauth_error=invalid_token"));
		Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Not.Contain(UiSessionAuthentication.CookieName + "="));
	}

	[Test]
	public async Task session_authentication_timeout_revokes_cookie_even_after_provider_recovers()
	{
		var signIn = HttpsContext();
		await UiSessionAuthentication.SignInAsync(signIn,
			new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test")), JwtToken);
		var cookie = signIn.Response.Headers.SetCookie.Last(x => x.StartsWith(UiSessionAuthentication.CookieName + "=")).Split(';')[0];
		var context = HttpsContext();
		context.Request.Headers.Cookie = cookie;
		_authenticationProvider.CompleteRequests = false;

		var result = await context.AuthenticateAsync(UiSessionAuthentication.Scheme);

		Assert.That(result.Succeeded, Is.False);
		Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Contain(UiSessionAuthentication.CookieName + "=;"));
		_authenticationProvider.CompleteRequests = true;
		var replay = HttpsContext();
		replay.Request.Headers.Cookie = cookie;
		Assert.That((await replay.AuthenticateAsync(UiSessionAuthentication.Scheme)).Succeeded, Is.False);
	}

	[Test]
	public void callback_request_cancellation_is_not_a_token_rejection()
	{
		using var service = Service(new TokenHandler());
		using var cancellation = new CancellationTokenSource();
		var context = CallbackContext(service);
		_authenticationProvider.CompleteRequests = false;
		_authenticationProvider.OnAuthenticate = cancellation.Cancel;

		Assert.That(async () => await service.HandleCallback(context, cancellation.Token),
			Throws.InstanceOf<OperationCanceledException>());
		Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
	}

	[Test]
	public async Task session_request_cancellation_does_not_revoke_session()
	{
		var signIn = HttpsContext();
		await UiSessionAuthentication.SignInAsync(signIn,
			new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test")), JwtToken);
		var cookie = signIn.Response.Headers.SetCookie.Last(x => x.StartsWith(UiSessionAuthentication.CookieName + "=")).Split(';')[0];
		using var cancellation = new CancellationTokenSource();
		var context = HttpsContext();
		context.Request.Headers.Cookie = cookie;
		context.RequestAborted = cancellation.Token;
		_authenticationProvider.CompleteRequests = false;
		_authenticationProvider.OnAuthenticate = cancellation.Cancel;

		Assert.That(async () => await context.AuthenticateAsync(UiSessionAuthentication.Scheme),
			Throws.InstanceOf<OperationCanceledException>());

		_authenticationProvider.CompleteRequests = true;
		_authenticationProvider.OnAuthenticate = null;
		var retry = HttpsContext();
		retry.Request.Headers.Cookie = cookie;
		Assert.That((await retry.AuthenticateAsync(UiSessionAuthentication.Scheme)).Succeeded, Is.True);
	}

	private DefaultHttpContext CallbackContext(OAuthBrowserFlowService service)
	{
		var challengeContext = HttpsContext();
		var challenge = service.CreateCodeChallenge(challengeContext);
		var context = HttpsContext();
		context.Request.Headers.Cookie = challengeContext.Response.Headers.SetCookie.ToString().Split(';')[0];
		context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
		{
			["code"] = "authorization-code",
			["state"] = State(challenge.CodeChallengeCorrelationId)
		});
		return context;
	}

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
	public async Task callback_exchanges_code_and_sets_protected_session_cookie()
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
		await AssertSessionCookie(context);
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
		Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/"));
		await AssertSessionCookie(context);
	}

	[Test]
	public async Task callback_rejects_opaque_token_response()
	{
		var handler = new TokenHandler("""{"access_token":"opaque-token"}""");
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
		Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/ui/signin?returnUrl=%2Fui%2Fstreams&oauth_error=unsupported_token"));
		Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Not.Contain($"{UiCredentialCookie.OAuthCookieName}="));
	}

	[Test]
	public async Task callback_rejects_token_that_fails_validation()
	{
		var handler = new TokenHandler($$"""{"access_token":"{{CreateToken(audience: "other-service")}}"}""");
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
		Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/ui/signin?returnUrl=%2Fui%2Fstreams&oauth_error=invalid_token"));
		Assert.That(context.Response.Headers.SetCookie.ToString(), Does.Not.Contain($"{UiCredentialCookie.OAuthCookieName}="));
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
		Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/?oauth_error=provider_error"));
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
		context.Request.Scheme = "https";
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
			new OAuthTokenValidator(Options(), _ => new ValueTask<TokenValidationParameters>(CreateValidationParameters())),
			adminUiEnabled);
	}

	private static string CreateToken(string audience)
	{
		var descriptor = new SecurityTokenDescriptor
		{
			Issuer = "https://login.example.test",
			Audience = audience,
			Subject = new ClaimsIdentity([new Claim("sub", "alice")]),
			NotBefore = DateTime.UtcNow.AddMinutes(-1),
			Expires = DateTime.UtcNow.AddMinutes(5),
			SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
		};

		return new JsonWebTokenHandler().CreateToken(descriptor);
	}

	private static TokenValidationParameters CreateValidationParameters() =>
		new()
		{
			ValidateIssuer = true,
			ValidIssuer = "https://login.example.test",
			ValidateAudience = true,
			ValidAudiences = ["eventstore"],
			ValidateIssuerSigningKey = true,
			IssuerSigningKey = SigningKey,
			ValidateLifetime = true,
			ClockSkew = TimeSpan.Zero,
			NameClaimType = "sub",
			RoleClaimType = "roles"
		};

	private async Task AssertSessionCookie(HttpContext context)
	{
		var cookies = context.Response.Headers.SetCookie;
		var session = cookies.Last(cookie => cookie.StartsWith(UiSessionAuthentication.CookieName + "=", StringComparison.Ordinal));
		Assert.That(session, Does.Contain("httponly"));
		Assert.That(session, Does.Contain("secure"));
		Assert.That(session, Does.Contain("samesite=lax"));
		Assert.That(cookies.ToString(), Does.Not.Contain(JwtToken));
		Assert.That(cookies.Single(cookie => cookie.StartsWith(UiCredentialCookie.OAuthCookieName + "=", StringComparison.Ordinal)),
			Does.StartWith(UiCredentialCookie.OAuthCookieName + "=;"));
		var authenticatedContext = HttpsContext();
		authenticatedContext.Request.Headers.Cookie = session.Split(';')[0];
		var result = await authenticatedContext.AuthenticateAsync(UiSessionAuthentication.Scheme);
		Assert.That(result.Succeeded, Is.True);
		Assert.That(result.Principal.Identity.Name, Is.EqualTo("alice"));
		Assert.That(result.Properties.ExpiresUtc - result.Properties.IssuedUtc,
			Is.LessThanOrEqualTo(UiSessionAuthentication.Lifetime));
	}

	private DefaultHttpContext HttpsContext()
	{
		var scope = _services.CreateScope();
		_requestScopes.Add(scope);
		var context = new DefaultHttpContext
		{
			RequestServices = scope.ServiceProvider
		};
		context.Request.Scheme = "https";
		context.Request.Host = new HostString("node.example.test");
		return context;
	}

	private sealed class ControlledAuthenticationProvider(IAuthenticationProvider inner) : AuthenticationProviderBase("test")
	{
		public bool CompleteRequests = true;
		public Action OnAuthenticate;
		public override IReadOnlyList<string> GetSupportedAuthenticationSchemes() => inner.GetSupportedAuthenticationSchemes();
		public override void Authenticate(AuthenticationRequest request)
		{
			OnAuthenticate?.Invoke();
			if (CompleteRequests)
				inner.Authenticate(request);
		}
	}

	private sealed class TokenHandler : HttpMessageHandler
	{
		private readonly string _response;
		public string Body { get; private set; } = "";

		public TokenHandler()
			: this($$"""{"access_token":"{{JwtToken}}"}""")
		{
		}

		public TokenHandler(string response) =>
			_response = response;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Body = await request.Content.ReadAsStringAsync(cancellationToken);
			return new(HttpStatusCode.OK)
			{
				Content = new StringContent(_response, Encoding.UTF8, "application/json")
			};
		}
	}
}
