namespace EventStore.Core.Authorization.AuthorizationPolicies;

public class AuthorizationPolicySettings(string streamAccessPolicyType)
{
	public string StreamAccessPolicyType { get; set; } = streamAccessPolicyType;

	public AuthorizationPolicySettings() : this(FallbackStreamAccessPolicySelector.FallbackPolicyName)
	{
	}

	public override string ToString() => $"{nameof(StreamAccessPolicyType)}: '{StreamAccessPolicyType}'";
}
