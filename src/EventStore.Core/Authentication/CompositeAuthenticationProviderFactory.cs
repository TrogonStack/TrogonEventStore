using System.Collections.Generic;
using System.Linq;
using EventStore.Plugins.Authentication;

namespace EventStore.Core.Authentication;

public class CompositeAuthenticationProviderFactory(IReadOnlyList<IAuthenticationProviderFactory> factories)
	: IAuthenticationProviderFactory
{
	public IAuthenticationProvider Build(bool logFailedAuthenticationAttempts)
	{
		var providers = factories
			.Select(factory => factory.Build(logFailedAuthenticationAttempts))
			.ToArray();

		return providers.Length == 1 ? providers[0] : new CompositeAuthenticationProvider(providers);
	}
}
