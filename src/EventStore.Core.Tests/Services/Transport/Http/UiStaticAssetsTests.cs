using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using EventStore.ClusterNode.Components;
using EventStore.ClusterNode.Components.Services;
using EventStore.Core.Authentication.PassthroughAuthentication;
using EventStore.Core.Services.Transport.Http;
using EventStore.Core.Services.Transport.Http.Authentication;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Http;

[TestFixture]
public class UiStaticAssetsTests
{
	private WebApplication _app;
	private HttpClient _client;
	private AssetAuthenticationProvider _authentication;
	private AssetSessionAuthenticator _sessions;

	[SetUp]
	public async Task SetUp()
	{
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			ApplicationName = typeof(App).Assembly.GetName().Name,
			ContentRootPath = TestContext.Parameters.Get("UiAssetsContentRoot", AppContext.BaseDirectory),
			EnvironmentName = "Production"
		});
		// Build manifests otherwise enable development-time cache overrides even in Production.
		builder.Configuration["ReloadStaticAssetsAtRuntime"] = "false";
		builder.Logging.ClearProviders();
		builder.WebHost.UseTestServer();
		builder.Services.AddRazorComponents();
		builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
		builder.Services.AddHttpContextAccessor();
		builder.Services.AddSingleton(new SecurityBrowserService(new PassthroughAuthenticationProvider(), false));
		_authentication = new AssetAuthenticationProvider();
		_sessions = new AssetSessionAuthenticator();
		builder.Services.AddSingleton<IAuthenticationProvider>(_authentication);
		builder.Services.AddSingleton<IUiSessionAuthenticator>(_sessions);
		builder.Services.AddSingleton<IReadOnlyList<IHttpAuthenticationProvider>>([
			new BasicHttpAuthenticationProvider(_authentication), new AnonymousHttpAuthenticationProvider()]);
		builder.Services.AddSingleton<AuthenticationMiddleware>();
		_app = builder.Build();
		_app.UseRouting();
		_app.UseMiddleware<AuthenticationMiddleware>();
		_app.UseAntiforgery();
		var manifestPath = TestContext.Parameters.Exists("UiAssetsContentRoot")
			? Path.Combine(_app.Environment.ContentRootPath, "EventStore.ClusterNode.staticwebassets.endpoints.json")
			: null;
		_app.MapStaticAssets(manifestPath).ShortCircuit();
		_app.MapRazorComponents<App>().WithStaticAssets(manifestPath);
		await _app.StartAsync();
		_client = _app.GetTestClient();
	}

	[TearDown]
	public async Task TearDown()
	{
		_client?.Dispose();
		if (_app is not null)
			await _app.DisposeAsync();
	}

	[TestCase("css/tailwind.generated.css", "text/css")]
	[TestCase("js/ui-auth.js", "text/javascript")]
	[TestCase("js/admin-operations.js", "text/javascript")]
	[TestCase("js/queue-dashboard.js", "text/javascript")]
	[TestCase("js/stream-browser.js", "text/javascript")]
	[TestCase("favicon.png", "image/png")]
	[TestCase("apple-touch-icon.png", "image/png")]
	[TestCase("es-tile.png", "image/png")]
	[TestCase("fonts/roboto-regular-webfont.woff2", "font/woff2")]
	public async Task packaged_assets_have_content_addressed_endpoints(string asset, string contentType)
	{
		var path = "ui/assets/" + asset;
		using var response = await _client.GetAsync("/" + path);
		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
		Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(contentType));
		Assert.That(response.Headers.ETag, Is.Not.Null);
		var extension = Path.GetExtension(path);
		var prefix = path[..^extension.Length] + ".";
		var routes = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints)
			.OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText);
		var fingerprintedPath = routes.Distinct().Single(route => route.StartsWith(prefix, StringComparison.Ordinal)
			&& route.EndsWith(extension, StringComparison.Ordinal) && route != path);
		using var fingerprinted = await _client.GetAsync("/" + fingerprintedPath);
		Assert.That(fingerprinted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
		Assert.That(await fingerprinted.Content.ReadAsByteArrayAsync(), Is.EqualTo(await response.Content.ReadAsByteArrayAsync()));
		Assert.That(fingerprinted.Headers.CacheControl?.Extensions.Any(extension => extension.Name == "immutable"), Is.True);
		using var conditional = new HttpRequestMessage(HttpMethod.Get, "/" + fingerprintedPath);
		conditional.Headers.IfNoneMatch.Add(fingerprinted.Headers.ETag);
		using var unchanged = await _client.SendAsync(conditional);
		Assert.That(unchanged.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
	}

	[Test]
	public async Task sign_in_page_references_fingerprinted_assets()
	{
		var html = await _client.GetStringAsync("/ui/signin");
		foreach (var asset in new[] { "css/tailwind.generated.css", "js/ui-auth.js", "js/admin-operations.js",
			"js/queue-dashboard.js", "js/stream-browser.js", "favicon.png", "apple-touch-icon.png", "es-tile.png" })
		{
			var path = "ui/assets/" + asset;
			var extension = Path.GetExtension(path);
			var prefix = path[..^extension.Length];
			Assert.That(html, Does.Match(System.Text.RegularExpressions.Regex.Escape(prefix) + @"\.[a-z0-9]+" +
				System.Text.RegularExpressions.Regex.Escape(extension)));
			Assert.That(html, Does.Not.Contain("\"/" + path + "\"").And.Not.Contain("\"" + path + "\""));
		}
	}

	[TestCase("Authorization", "Basic YWRtaW46d3Jvbmc=")]
	[TestCase("Cookie", "session=invalid")]
	public async Task public_assets_do_not_authenticate_credentials_or_sessions(string header, string value)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/ui/assets/js/ui-auth.js");
		request.Headers.Add(header, value);
		using var response = await _client.SendAsync(request);
		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
		Assert.That(_authentication.Checks, Is.Zero);
		Assert.That(_sessions.Checks, Is.Zero);
	}

	[Test]
	public async Task non_asset_ui_requests_still_authenticate()
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "/ui/signin");
		request.Headers.Add("Authorization", "Basic YWRtaW46d3Jvbmc=");
		using var response = await _client.SendAsync(request);
		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
		Assert.That(_authentication.Checks, Is.EqualTo(1));
	}

	private sealed class AssetAuthenticationProvider() : AuthenticationProviderBase("test")
	{
		public int Checks;
		public override void Authenticate(AuthenticationRequest request)
		{
			Checks++;
			request.Unauthorized();
		}
		public override IReadOnlyList<string> GetSupportedAuthenticationSchemes() => ["Basic"];
	}

	private sealed class AssetSessionAuthenticator : IUiSessionAuthenticator
	{
		public int Checks;
		public Task<ClaimsPrincipal> AuthenticateAsync(HttpContext context)
		{
			Checks++;
			return Task.FromResult<ClaimsPrincipal>(null);
		}
		public Task<bool> ValidateRequestAsync(HttpContext context) => Task.FromResult(true);
	}
}
