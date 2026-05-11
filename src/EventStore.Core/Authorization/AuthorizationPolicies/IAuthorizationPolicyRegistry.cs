using System.Threading.Tasks;

namespace EventStore.Core.Authorization.AuthorizationPolicies;

public interface IAuthorizationPolicyRegistry
{
	public ReadOnlyPolicy[] EffectivePolicies { get; }
	public Task Start();
	public Task Stop();
}
