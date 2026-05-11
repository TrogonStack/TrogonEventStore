using EventStore.Core.Authorization.AuthorizationPolicies;
using EventStore.Plugins.Authorization;

namespace EventStore.Core.Authorization;

public class InternalAuthorizationProviderFactory(IAuthorizationPolicyRegistry registry) : IAuthorizationProviderFactory
{
	public IAuthorizationProvider Build()
	{
		return new PolicyAuthorizationProvider(
			new MultiPolicyEvaluator(registry), logAuthorization: true, logSuccesses: false);
	}
}
