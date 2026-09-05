using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EventStore.ClusterNode.Components.Services;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EventStore.Core.Tests.Authentication;

[TestFixture]
public class UiSessionSecurityTests
{
	[Test]
	public async Task legacy_password_cookie_cannot_authenticate_a_request()
	{
		var context = new DefaultHttpContext();
		context.Request.Scheme = "https";
		context.Request.Path = "/ui";
		var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret-password"));
		context.Request.Headers.Cookie = "es-creds=" + Uri.EscapeDataString(
			JsonSerializer.Serialize(new { credentials }));
		var middleware = new UiCredentialsMiddleware(_ => Task.CompletedTask);

		await middleware.InvokeAsync(context);

		Assert.That(context.Request.Headers.Authorization.ToString(), Is.Empty,
			"Browser cookies must never be promoted into reusable Basic credentials.");
		Assert.That(context.User.Identity?.IsAuthenticated, Is.Not.True);
	}
}
