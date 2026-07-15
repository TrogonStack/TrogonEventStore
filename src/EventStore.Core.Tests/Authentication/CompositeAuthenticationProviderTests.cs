using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

	[Test]
	public async Task routes_certificate_requests_to_user_certificate_provider()
	{
		var bearerProvider = new RecordingAuthenticationProvider(["Bearer"]);
		var certificateProvider = new RecordingAuthenticationProvider(["UserCertificate"]);
		var provider = new CompositeAuthenticationProvider([bearerProvider, certificateProvider]);
		var context = new DefaultHttpContext();
		var request = HttpAuthenticationRequest.CreateWithValidCertificate(context, "admin", CreateCertificate());

		provider.Authenticate(request);
		await request.AuthenticateAsync();

		Assert.AreEqual(0, bearerProvider.Calls);
		Assert.AreEqual(1, certificateProvider.Calls);
	}

	[Test]
	public async Task does_not_route_password_requests_to_user_certificate_provider()
	{
		var bearerProvider = new RecordingAuthenticationProvider(["Bearer"]);
		var certificateProvider = new RecordingAuthenticationProvider(["UserCertificate"]);
		var provider = new CompositeAuthenticationProvider([bearerProvider, certificateProvider]);
		var request = new HttpAuthenticationRequest(new DefaultHttpContext(), "admin", "changeit");

		provider.Authenticate(request);
		var status = await request.AuthenticateAsync();

		Assert.AreEqual(HttpAuthenticationRequestStatus.Unauthenticated, status.Item1);
		Assert.AreEqual(0, bearerProvider.Calls);
		Assert.AreEqual(0, certificateProvider.Calls);
	}

	private static X509Certificate2 CreateCertificate()
	{
		using var rsa = RSA.Create();
		var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
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
