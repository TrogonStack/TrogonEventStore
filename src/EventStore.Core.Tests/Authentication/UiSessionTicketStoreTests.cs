using System;
using System.Security.Claims;
using System.Threading.Tasks;
using EventStore.ClusterNode.Components.Services;
using Microsoft.AspNetCore.Authentication;
using NUnit.Framework;

namespace EventStore.Core.Tests.Authentication;

[TestFixture]
public class UiSessionTicketStoreTests
{
	[Test]
	public async Task capacity_exhaustion_fails_instead_of_issuing_an_unusable_session()
	{
		using var store = new UiSessionTicketStore();
		var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("test")),
			new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(15) }, "test");
		for (var i = 0; i < 10_000; i++)
			await store.StoreAsync(ticket);

		Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync(ticket));
	}
}
