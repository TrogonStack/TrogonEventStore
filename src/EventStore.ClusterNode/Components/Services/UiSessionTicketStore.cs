using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;

namespace EventStore.ClusterNode.Components.Services;

public sealed class UiSessionTicketStore : IDisposable
{
	private readonly MemoryCache _tickets = new(new MemoryCacheOptions { SizeLimit = 10_000 });

	public Task<string> StoreAsync(AuthenticationTicket ticket)
	{
		var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
		if (ticket.Properties.ExpiresUtc is not { } expires)
			throw new InvalidOperationException("UI sessions require an absolute expiration.");
		_tickets.Set(key, TicketSerializer.Default.Serialize(ticket),
			new MemoryCacheEntryOptions { AbsoluteExpiration = expires, Size = 1 });
		if (!_tickets.TryGetValue(key, out _))
			throw new InvalidOperationException("The node cannot accept another management session. Try again later.");
		return Task.FromResult(key);
	}

	public Task<AuthenticationTicket> RetrieveAsync(string key) =>
		Task.FromResult(_tickets.TryGetValue(key, out byte[] ticket)
			? TicketSerializer.Default.Deserialize(ticket)
			: null);

	public Task RemoveAsync(string key)
	{
		_tickets.Remove(key);
		return Task.CompletedTask;
	}

	public void Dispose() => _tickets.Dispose();
}
