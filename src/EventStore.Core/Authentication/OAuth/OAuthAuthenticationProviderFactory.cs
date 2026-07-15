using EventStore.Plugins.Authentication;

namespace EventStore.Core.Authentication.OAuth;

public class OAuthAuthenticationProviderFactory(ClusterVNodeOptions.OAuthOptions options) : IAuthenticationProviderFactory
{
	public IAuthenticationProvider Build(bool logFailedAuthenticationAttempts) =>
		new OAuthAuthenticationProvider(options, logFailedAuthenticationAttempts);
}
