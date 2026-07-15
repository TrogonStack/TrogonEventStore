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
		]));

		var info = service.AuthenticationInfo();

		Assert.That(info.SupportsOAuthBrowserFlow, Is.False);
	}

	private sealed class BrowserFlowAuthenticationProvider(
		IReadOnlyList<KeyValuePair<string, string>> publicProperties)
		: AuthenticationProviderBase(name: "test")
	{
		public override void Authenticate(AuthenticationRequest authenticationRequest) =>
			authenticationRequest.Authenticated(
				new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test")));

		public override IReadOnlyList<string> GetSupportedAuthenticationSchemes() => ["Bearer"];

		public override IEnumerable<KeyValuePair<string, string>> GetPublicProperties() => publicProperties;
	}
}
