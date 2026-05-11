namespace EventStore.Core.Authorization.AuthorizationPolicies;

public class StaticPolicySelector(ReadOnlyPolicy policy) : IPolicySelector
{
	public ReadOnlyPolicy Select() => policy;
}
