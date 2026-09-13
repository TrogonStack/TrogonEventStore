using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace EventStore.TestClient.Tests;

[TestFixture]
public class CommandInventoryTests
{
	private static readonly string[] SupportedCommandKeywords =
	[
		"PING", "PINGFL", "PINGFLW", "RDALLGRPC", "WRFLGRPC",
		"WR", "WRJ", "WRFL", "WRFLW",
		"MWR", "MWRFLW", "DEL",
		"RDALL", "RD", "RDFL", "WRLT",
		"VERIFY", "SUBSCR", "SCAVENGE", "CHKGRPC", "SST"
	];
	private static readonly string[] RetiredCommandKeywords = ["TWR", "WRFLCA", "WRFLTCP", "RT", "CHKTCP"];

	[Test]
	public void test_client_preserves_supported_command_surface()
	{
		using var cancellation = new CancellationTokenSource();
		var client = new Client(new ClientOptions(), cancellation);
		var usages = client.GetCommandList()
			.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		Assert.Multiple(() =>
		{
			foreach (var keyword in SupportedCommandKeywords)
			{
				Assert.That(usages.Any(x => x == keyword || x.StartsWith($"{keyword} ", StringComparison.Ordinal)),
					Is.True, $"Missing TestClient command '{keyword}'");
			}

			foreach (var keyword in RetiredCommandKeywords)
			{
				Assert.That(usages.Any(x => x == keyword || x.StartsWith($"{keyword} ", StringComparison.Ordinal)),
					Is.False, $"TestClient command '{keyword}' advertises behavior that gRPC does not provide");
			}
		});
	}
}
