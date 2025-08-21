#nullable enable
namespace EventStore.Core.Authorization.AuthorizationPolicies;

public interface IPolicySelector
{
	ReadOnlyPolicy Select();
}
