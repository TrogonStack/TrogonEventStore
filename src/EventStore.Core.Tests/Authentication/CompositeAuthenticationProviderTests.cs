using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using EventStore.Core.Authentication;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EventStore.Core.Tests.Authentication;

[TestFixture]
public class CompositeAuthenticationProviderTests
{
	[Test]
	public async Task routes_bearer_requests_to_bearer_provider()
	{
		var basicProvider = new RecordingAuthenticationProvider(["Basic"]);
		var bearerProvider = new RecordingAuthenticationProvider(["Bearer"]);
		var provider = new CompositeAuthenticationProvider([basicProvider, bearerProvider]);
		var request = new HttpAuthenticationRequest(new DefaultHttpContext(), "jwt-token");

		provider.Authenticate(request);
		await request.AuthenticateAsync();

		Assert.AreEqual(0, basicProvider.Calls);
		Assert.AreEqual(1, bearerProvider.Calls);
	}

	[Test]
	public async Task routes_password_requests_to_basic_provider()
	{
		var basicProvider = new RecordingAuthenticationProvider(["Basic"]);
		var bearerProvider = new RecordingAuthenticationProvider(["Bearer"]);
		var provider = new CompositeAuthenticationProvider([basicProvider, bearerProvider]);
		var request = new HttpAuthenticationRequest(new DefaultHttpContext(), "admin", "changeit");

		provider.Authenticate(request);
		await request.AuthenticateAsync();

		Assert.AreEqual(1, basicProvider.Calls);
		Assert.AreEqual(0, bearerProvider.Calls);
	}

	private sealed class RecordingAuthenticationProvider(IReadOnlyList<string> schemes) : AuthenticationProviderBase
	{
		public int Calls { get; private set; }

		public override void Authenticate(AuthenticationRequest authenticationRequest)
		{
			Calls++;
			authenticationRequest.Authenticated(
				new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test")));
		}

		public override IReadOnlyList<string> GetSupportedAuthenticationSchemes() => schemes;
	}
}
