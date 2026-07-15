using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Authentication.OAuth;
using EventStore.Core.Services;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EventStore.Core.Tests.Authentication;

[TestFixture]
public class OAuthAuthenticationProviderTests
{
	[Test]
	public async Task authenticates_valid_bearer_token_and_maps_roles()
	{
		var signingKey = new SymmetricSecurityKey(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
		var token = CreateToken(signingKey, audience: "eventstore");
		var provider = new OAuthAuthenticationProvider(
			new()
			{
				Issuer = "https://login.example.test",
				Audiences = ["eventstore"],
				NameClaimType = "sub",
				RoleClaimType = "roles"
			},
			logFailedAuthenticationAttempts: false,
			_ => new ValueTask<TokenValidationParameters>(CreateValidationParameters(signingKey)));

		var request = new HttpAuthenticationRequest(new DefaultHttpContext(), token);
		provider.Authenticate(request);

		var (status, principal) = await request.AuthenticateAsync();

		Assert.AreEqual(HttpAuthenticationRequestStatus.Authenticated, status);
		Assert.AreEqual("alice", principal.Identity?.Name);
		Assert.That(principal.HasClaim(ClaimTypes.Role, SystemRoles.Admins), Is.True);
	}

	[Test]
	public async Task rejects_token_with_wrong_audience()
	{
		var signingKey = new SymmetricSecurityKey(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
		var token = CreateToken(signingKey, audience: "other-service");
		var provider = new OAuthAuthenticationProvider(
			new()
			{
				Issuer = "https://login.example.test",
				Audiences = ["eventstore"],
				NameClaimType = "sub",
				RoleClaimType = "roles"
			},
			logFailedAuthenticationAttempts: false,
			_ => new ValueTask<TokenValidationParameters>(CreateValidationParameters(signingKey)));

		var request = new HttpAuthenticationRequest(new DefaultHttpContext(), token);
		provider.Authenticate(request);

		var (status, _) = await request.AuthenticateAsync();

		Assert.AreEqual(HttpAuthenticationRequestStatus.Unauthenticated, status);
	}

	[Test]
	public void does_not_advertise_browser_flow_without_client_id()
	{
		var provider = new OAuthAuthenticationProvider(
			new()
			{
				Issuer = "https://login.example.test",
				Audiences = ["eventstore"]
			},
			logFailedAuthenticationAttempts: false,
			_ => new ValueTask<TokenValidationParameters>(CreateValidationParameters(new SymmetricSecurityKey(new byte[32]))));

		var properties = provider.GetPublicProperties().ToDictionary(x => x.Key, x => x.Value);

		Assert.That(properties.ContainsKey("authorization_endpoint"), Is.False);
		Assert.That(properties.ContainsKey("client_id"), Is.False);
	}

	[Test]
	public void advertises_configured_browser_flow_properties()
	{
		var provider = new OAuthAuthenticationProvider(
			new()
			{
				Issuer = "https://login.example.test",
				Audiences = ["eventstore"],
				AuthorizationEndpoint = "https://login.example.test/oauth2/auth",
				TokenEndpoint = "https://login.example.test/oauth2/token",
				ClientId = "eventstore-ui",
				Scopes = ["openid", "profile", "roles"],
				CodeChallengePath = "/custom/challenge",
				RedirectPath = "/custom/callback"
			},
			logFailedAuthenticationAttempts: false,
			_ => new ValueTask<TokenValidationParameters>(CreateValidationParameters(new SymmetricSecurityKey(new byte[32]))));

		var properties = provider.GetPublicProperties().ToDictionary(x => x.Key, x => x.Value);

		Assert.AreEqual("https://login.example.test/oauth2/auth", properties["authorization_endpoint"]);
		Assert.AreEqual("eventstore-ui", properties["client_id"]);
		Assert.AreEqual("/custom/challenge", properties["code_challenge_uri"]);
		Assert.AreEqual("/custom/callback", properties["redirect_uri"]);
		Assert.AreEqual("code", properties["response_type"]);
		Assert.AreEqual("openid profile roles", properties["scope"]);
	}

	private static string CreateToken(SecurityKey signingKey, string audience)
	{
		var descriptor = new SecurityTokenDescriptor
		{
			Issuer = "https://login.example.test",
			Audience = audience,
			Subject = new ClaimsIdentity([
				new Claim("sub", "alice"),
				new Claim("roles", SystemRoles.Admins)
			]),
			NotBefore = DateTime.UtcNow.AddMinutes(-1),
			Expires = DateTime.UtcNow.AddMinutes(5),
			SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
		};

		return new JsonWebTokenHandler().CreateToken(descriptor);
	}

	private static TokenValidationParameters CreateValidationParameters(SecurityKey signingKey) =>
		new()
		{
			ValidateIssuer = true,
			ValidIssuer = "https://login.example.test",
			ValidateAudience = true,
			ValidAudiences = ["eventstore"],
			ValidateIssuerSigningKey = true,
			IssuerSigningKey = signingKey,
			ValidateLifetime = true,
			ClockSkew = TimeSpan.Zero,
			NameClaimType = "sub",
			RoleClaimType = "roles"
		};
}
