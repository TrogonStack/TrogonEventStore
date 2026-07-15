using EventStore.Core.Services.Transport.Http.Authentication;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Http.Authentication;

[TestFixture]
public class bearer_http_authentication_provider_should
{
	[Test]
	public void pass_bearer_token_as_jwt_to_authentication_provider()
	{
		var authenticationProvider = new RecordingAuthenticationProvider();
		var provider = new BearerHttpAuthenticationProvider(authenticationProvider);
		var context = new DefaultHttpContext();
		context.Request.Headers.Authorization = "Bearer access-token";

		var handled = provider.Authenticate(context, out var request);

		Assert.True(handled);
		Assert.NotNull(request);
		Assert.AreEqual(1, authenticationProvider.Calls);
		Assert.AreEqual("access-token", authenticationProvider.Token);
	}

	[Test]
	public void ignore_non_bearer_authorization_header()
	{
		var authenticationProvider = new RecordingAuthenticationProvider();
		var provider = new BearerHttpAuthenticationProvider(authenticationProvider);
		var context = new DefaultHttpContext();
		context.Request.Headers.Authorization = "Basic credentials";

		var handled = provider.Authenticate(context, out var request);

		Assert.False(handled);
		Assert.Null(request);
		Assert.AreEqual(0, authenticationProvider.Calls);
	}

	private sealed class RecordingAuthenticationProvider : AuthenticationProviderBase
	{
		public int Calls { get; private set; }
		public string Token { get; private set; }

		public override void Authenticate(AuthenticationRequest authenticationRequest)
		{
			Calls++;
			Token = authenticationRequest.GetToken("jwt");
			authenticationRequest.Unauthorized();
		}
	}
}
