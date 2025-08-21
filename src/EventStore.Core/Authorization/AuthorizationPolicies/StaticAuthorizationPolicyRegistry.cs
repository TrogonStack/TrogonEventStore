#nullable enable
using System.Linq;
using System.Threading.Tasks;

namespace EventStore.Core.Authorization.AuthorizationPolicies;

public class StaticAuthorizationPolicyRegistry(IPolicySelector[] policySelectors) : IAuthorizationPolicyRegistry
{
	public Task Start() => Task.CompletedTask;
	public Task Stop() => Task.CompletedTask;
	public ReadOnlyPolicy[] EffectivePolicies => policySelectors.Select(x => x.Select()).ToArray();
}
