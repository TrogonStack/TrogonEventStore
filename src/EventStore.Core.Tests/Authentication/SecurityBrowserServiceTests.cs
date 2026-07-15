using System.Collections.Generic;
using System.Security.Claims;
using EventStore.ClusterNode.Components.Services;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EventStore.Core.Tests.Authentication;

[TestFixture]
public class SecurityBrowserServiceTests
{
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
