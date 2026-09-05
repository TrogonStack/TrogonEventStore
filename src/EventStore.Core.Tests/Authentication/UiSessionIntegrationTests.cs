using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.ClusterNode.Components.Services;
using EventStore.Core.Authentication;
using EventStore.Core.Authentication.InternalAuthentication;
using EventStore.Core.Services.Transport.Http;
using EventStore.Core.Services.Transport.Http.Authentication;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EventStore.Core.Tests.Authentication;

[TestFixture]
public class UiSessionIntegrationTests
{
	private TestServer _server;
	private IHost _host;
	private HttpClient _client;
	private SessionProvider _provider;
	private SessionClock _clock;

	[SetUp]
	public async Task SetUp()
	{
		_provider = new SessionProvider();
		_clock = new SessionClock();
		_host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
			.ConfigureServices(services =>
			{
				services.AddRouting();
				services.AddLogging();
				services.AddDataProtection();
				services.AddSingleton<IAuthenticationProvider>(_provider);
				services.AddSingleton<IReadOnlyList<IHttpAuthenticationProvider>>([
					new BasicHttpAuthenticationProvider(_provider), new AnonymousHttpAuthenticationProvider()]);
				services.AddSingleton<EventStore.Core.Services.Transport.Http.AuthenticationMiddleware>();
				services.AddAuthentication(options =>
				{
					options.DefaultAuthenticateScheme = "es auth";
					options.DefaultChallengeScheme = "es auth";
					options.AddScheme<EventStoreAuthenticationHandler>("es auth", null);
				});
				services.AddAuthorization();
				services.AddAntiforgery();
				services.AddUiSessionAuthentication();
				services.Configure<CookieAuthenticationOptions>(UiSessionAuthentication.Scheme, options => options.TimeProvider = _clock);
			})
			.Configure(app =>
			{
				app.UseMiddleware<UiCredentialsMiddleware>();
				app.UseMiddleware<EventStore.Core.Services.Transport.Http.AuthenticationMiddleware>();
				app.UseAuthentication();
				app.UseRouting();
				app.UseAuthorization();
				app.UseAntiforgery();
				app.UseEndpoints(endpoints =>
				{
					endpoints.MapPost("/ui/login", async context =>
					{
						var result = await new SecurityBrowserService(_provider, true).SignInAsync(context, "admin", "correct-password");
						context.Response.StatusCode = result.Success ? 204 : 401;
					});
					endpoints.MapGet("/ui/me", context => context.Response.WriteAsync(context.User.Identity?.Name ?? "anonymous"));
					endpoints.MapGet("/outside", context => context.Response.WriteAsync(context.User.Identity?.Name ?? "anonymous"));
					endpoints.MapGet("/ui/csrf", context => context.Response.WriteAsync(
						context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken));
					endpoints.MapPost("/ui/change", context => { context.Response.StatusCode = 204; return Task.CompletedTask; }).RequireAuthorization();
					endpoints.MapPost("/ui/logout", context => context.SignOutAsync(UiSessionAuthentication.Scheme)).RequireAuthorization();
				});
			})).StartAsync();
		_server = _host.GetTestServer();
		_client = _server.CreateClient();
		_client.BaseAddress = new Uri("https://localhost");
	}

	[TearDown]
	public void TearDown()
	{
		_client.Dispose();
		_host.Dispose();
	}

	[Test]
	public async Task login_issues_protected_identifier_without_password_or_identity_in_browser()
	{
		var response = await _client.PostAsync("/ui/login", null);
		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
		var header = response.Headers.GetValues("Set-Cookie").Last(x => x.StartsWith(UiSessionAuthentication.CookieName + "="));
		Assert.That(header, Does.Contain("secure").And.Contain("httponly").And.Contain("samesite=lax"));
		Assert.That(header, Does.Not.Contain("correct-password").And.Not.Contain("admin"));
		var cookie = header.Split(';')[0];
		var options = _server.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(UiSessionAuthentication.Scheme);
		var browserTicket = options.TicketDataFormat.Unprotect(Uri.UnescapeDataString(cookie[(cookie.IndexOf('=') + 1)..]));
		Assert.That(browserTicket, Is.Not.Null);
		Assert.That(browserTicket.Principal.Identity?.Name, Is.Null);
		Assert.That(browserTicket.Properties.Items.Values, Has.None.Contains("correct-password"));
		_client.DefaultRequestHeaders.Add("Cookie", cookie);
		Assert.That(await _client.GetStringAsync("/ui/me"), Is.EqualTo("admin"));
		Assert.That(_provider.PasswordChecks, Is.EqualTo(1));
		Assert.That(_provider.SessionChecks, Is.EqualTo(1));
	}

	[Test]
	public async Task signing_in_again_rotates_session_and_does_not_revive_old_cookie()
	{
		await Login();
		_client.DefaultRequestHeaders.Add("Authorization", "Basic YWRtaW46Y29ycmVjdC1wYXNzd29yZA==");
		var response = await _client.PostAsync("/ui/login", null);
		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
		_client.DefaultRequestHeaders.Remove("Authorization");
		Assert.That(await _client.GetStringAsync("/ui/me"), Is.EqualTo("anonymous"));
		_client.DefaultRequestHeaders.Remove("Cookie");
		_client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie")
			.Last(x => x.StartsWith(UiSessionAuthentication.CookieName + "=")).Split(';')[0]);
		Assert.That(await _client.GetStringAsync("/ui/me"), Is.EqualTo("admin"));
	}

	[Test]
	public async Task session_expires_absolutely_without_sliding_renewal()
	{
		await Login();
		_clock.Advance(TimeSpan.FromMinutes(14));
		Assert.That(await _client.GetStringAsync("/ui/me"), Is.EqualTo("admin"));
		_clock.Advance(TimeSpan.FromMinutes(2));
		Assert.That(await _client.GetStringAsync("/ui/me"), Is.EqualTo("anonymous"));
	}

	[Test]
	public async Task revoked_account_cannot_reuse_session_even_if_reenabled_later()
	{
		await Login();
		_provider.Valid = false;
		Assert.That(await _client.GetStringAsync("/ui/me"), Is.EqualTo("anonymous"));
		_provider.Valid = true;
		Assert.That(await _client.GetStringAsync("/ui/me"), Is.EqualTo("anonymous"));
	}

	[TestCase(true)]
	[TestCase(false)]
	public async Task cancelled_password_session_validation_does_not_revoke_cookie(bool alreadyCancelled)
	{
		var cookie = await Login();
		using var cancellation = new CancellationTokenSource();
		var validationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_provider.ValidateSession = async token =>
		{
			validationStarted.SetResult();
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, token);
			}
			catch (OperationCanceledException)
			{
				return null;
			}
			return null;
		};
		using var scope = _server.Services.CreateScope();
		var context = new DefaultHttpContext
		{
			RequestServices = scope.ServiceProvider,
			RequestAborted = cancellation.Token
		};
		context.Request.Scheme = "https";
		context.Request.Headers.Cookie = cookie;
		if (alreadyCancelled)
			cancellation.Cancel();
		var authentication = context.AuthenticateAsync(UiSessionAuthentication.Scheme);
		await validationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cancellation.Cancel();
		Assert.That(async () => await authentication, Throws.InstanceOf<OperationCanceledException>());
		Assert.That(context.Response.Headers.SetCookie, Is.Empty);
		_provider.ValidateSession = null;
		Assert.That(await _client.GetStringAsync("/ui/me"), Is.EqualTo("admin"));
	}

	[Test]
	public async Task csrf_is_required_for_cookie_authenticated_mutations_and_logout_revokes_replay()
	{
		var cookie = await Login();
		Assert.That((await _client.PostAsync("/ui/change", null)).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
		var csrf = await _client.GetAsync("/ui/csrf");
		var token = await csrf.Content.ReadAsStringAsync();
		_client.DefaultRequestHeaders.Remove("Cookie");
		_client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + csrf.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
		_client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
		Assert.That((await _client.PostAsync("/ui/change", null)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
		Assert.That((await _client.PostAsync("/ui/logout", null)).IsSuccessStatusCode, Is.True);
		Assert.That(await _client.GetStringAsync("/ui/me"), Is.EqualTo("anonymous"));
	}

	[Test]
	public async Task cookie_does_not_authenticate_outside_ui_or_override_explicit_credentials()
	{
		await Login();
		Assert.That(await _client.GetStringAsync("/outside"), Is.EqualTo("anonymous"));
		_client.DefaultRequestHeaders.Add("Authorization", "Basic YWRtaW46d3Jvbmc=");
		Assert.That((await _client.GetAsync("/ui/me")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
	}

	[Test]
	public async Task tampered_cookie_cannot_authenticate()
	{
		var cookie = await Login();
		_client.DefaultRequestHeaders.Remove("Cookie");
		_client.DefaultRequestHeaders.Add("Cookie", cookie.Insert(cookie.IndexOf('=') + 10, "tampered"));
		Assert.That(await _client.GetStringAsync("/ui/me"), Is.EqualTo("anonymous"));
	}

	[Test]
	public async Task http_cannot_issue_or_use_a_browser_session()
	{
		await Login();
		Assert.That(await _client.GetStringAsync("http://localhost/ui/me"), Is.EqualTo("anonymous"));
		Assert.That((await _client.PostAsync("http://localhost/ui/login", null)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
	}

	private async Task<string> Login()
	{
		var response = await _client.PostAsync("/ui/login", null);
		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
		var cookie = response.Headers.GetValues("Set-Cookie").Last(x => x.StartsWith(UiSessionAuthentication.CookieName + "=")).Split(';')[0];
		_client.DefaultRequestHeaders.Add("Cookie", cookie);
		return cookie;
	}

	private sealed class SessionClock : TimeProvider
	{
		private DateTimeOffset _now = DateTimeOffset.UtcNow;
		public override DateTimeOffset GetUtcNow() => _now;
		public void Advance(TimeSpan amount) => _now += amount;
	}

	private sealed class SessionProvider : AuthenticationProviderBase, ISessionAuthenticationProvider
	{
		public bool Valid = true;
		public int PasswordChecks;
		public int SessionChecks;
		public Func<CancellationToken, Task<ClaimsPrincipal>> ValidateSession;
		public SessionProvider() : base("test") { }
		public void AuthenticateSession(AuthenticationRequest request)
		{
			SessionChecks++;
			Authenticate(request);
		}
		public override IReadOnlyList<string> GetSupportedAuthenticationSchemes() => ["Basic"];
		public override void Authenticate(AuthenticationRequest request)
		{
			PasswordChecks++;
			if (request.SuppliedPassword == "correct-password")
				request.Authenticated(Principal());
			else
				request.Unauthorized();
		}
		public Task<ClaimsPrincipal> ValidateSessionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
			ValidateSession?.Invoke(cancellationToken) ?? Task.FromResult(Valid ? Principal() : null);
		private static ClaimsPrincipal Principal() => new(new ClaimsIdentity([
			new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "$admins"),
			new Claim(InternalAuthenticationProvider.SessionSecurityStampClaimType, "verified-revision")], "test"));
	}
}
