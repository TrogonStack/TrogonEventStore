using System.Threading.Tasks;
using EventStore.Core.Bus;

namespace EventStore.Core.Authorization.AuthorizationPolicies;

public interface IPolicySelectorFactory
{
	string CommandLineName { get; }
	IPolicySelector Create(IPublisher publisher);
	Task<bool> Enable();
	Task Disable();
}
