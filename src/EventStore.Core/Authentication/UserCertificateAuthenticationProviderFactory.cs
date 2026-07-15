using EventStore.Plugins.Authentication;

namespace EventStore.Core.Authentication;

public class UserCertificateAuthenticationProviderFactory(IAuthenticationProviderFactory innerFactory)
	: IAuthenticationProviderFactory
{
	public IAuthenticationProvider Build(bool logFailedAuthenticationAttempts) =>
		new UserCertificateAuthenticationProvider(innerFactory.Build(logFailedAuthenticationAttempts));
}
