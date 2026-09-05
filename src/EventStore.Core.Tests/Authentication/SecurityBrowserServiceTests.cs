using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.ClusterNode.Components.Services;
using EventStore.Core.Authentication;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EventStore.Core.Tests.Authentication;

[TestFixture]
public class SecurityBrowserServiceTests
{
	[Test]
	public async Task password_sign_in_timeout_returns_not_ready_without_issuing_cookie()
	{
		var provider = new UnresponsiveSessionAuthenticationProvider();
		var service = new SecurityBrowserService(provider, supportsPassword: true);
		var context = new DefaultHttpContext();
		context.Request.Scheme = "https";

		var result = await service.SignInAsync(context, "test", "password");

		Assert.That(result, Is.EqualTo(SecurityCommandResult.Failure("The authentication provider is not ready yet.")));
		Assert.That(context.Response.Headers.SetCookie, Is.Empty);
		Assert.That(provider.SessionValidationCalls, Is.Zero);
	}

	[Test]
	public void password_sign_in_request_cancellation_propagates_without_issuing_cookie()
	{
		var provider = new UnresponsiveSessionAuthenticationProvider();
		var service = new SecurityBrowserService(provider, supportsPassword: true);
		using var cancellation = new CancellationTokenSource();
		var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
		context.Request.Scheme = "https";
		cancellation.Cancel();

		Assert.That(async () => await service.SignInAsync(context, "test", "password"),
			Throws.InstanceOf<OperationCanceledException>());
		Assert.That(context.Response.Headers.SetCookie, Is.Empty);
		Assert.That(provider.SessionValidationCalls, Is.Zero);
	}

	[Test]
	public void does_not_enable_oauth_browser_flow_with_blank_scope()
	{
		var service = new SecurityBrowserService(new BrowserFlowAuthenticationProvider([
			new("authorization_endpoint", "https://login.example.test/oauth2/auth"),
			new("client_id", "eventstore-ui"),
			new("code_challenge_uri", "/oauth/challenge"),
			new("redirect_uri", "/oauth/callback"),
			new("response_type", "code"),
			new("scope", "")
		]), supportsPassword: false);

		var info = service.AuthenticationInfo();

		Assert.That(info.SupportsOAuthBrowserFlow, Is.False);
	}

	[Test]
	public void does_not_enable_basic_for_certificate_only_transport_scheme()
	{
		var service = new SecurityBrowserService(
			new BrowserFlowAuthenticationProvider([], ["Basic", "UserCertificate"]),
			supportsPassword: false);

		var info = service.AuthenticationInfo();

		Assert.That(info.SupportsBasic, Is.False);
	}

	[Test]
	public void enables_basic_when_password_authentication_is_configured()
	{
		var service = new SecurityBrowserService(
			new BrowserFlowAuthenticationProvider([], ["Basic", "UserCertificate"]),
			supportsPassword: true);

		var info = service.AuthenticationInfo();

		Assert.That(info.SupportsBasic, Is.True);
	}

	private sealed class UnresponsiveSessionAuthenticationProvider()
		: AuthenticationProviderBase(name: "test"), ISessionAuthenticationProvider
	{
		public int SessionValidationCalls { get; private set; }

		public override IReadOnlyList<string> GetSupportedAuthenticationSchemes() => ["Basic"];

		public override void Authenticate(AuthenticationRequest authenticationRequest) =>
			throw new InvalidOperationException("Browser sign-in must use session authentication.");

		public void AuthenticateSession(AuthenticationRequest authenticationRequest) { }

		public Task<ClaimsPrincipal> ValidateSessionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
		{
			SessionValidationCalls++;
			return Task.FromResult<ClaimsPrincipal>(null);
		}
	}

	private sealed class BrowserFlowAuthenticationProvider(
		IReadOnlyList<KeyValuePair<string, string>> publicProperties,
		IReadOnlyList<string> schemes = null)
		: AuthenticationProviderBase(name: "test")
	{
		public override void Authenticate(AuthenticationRequest authenticationRequest) =>
			authenticationRequest.Authenticated(
				new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test")));

		public override IReadOnlyList<string> GetSupportedAuthenticationSchemes() => schemes ?? ["Bearer"];

		public override IEnumerable<KeyValuePair<string, string>> GetPublicProperties() => publicProperties;
	}
}
