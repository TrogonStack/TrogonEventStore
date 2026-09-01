using EventStore.Plugins.Authorization;

namespace EventStore.Core.Authorization;

internal static class ForwardingOperations
{
	public static readonly OperationDefinition Connect = new("node/forwarding", "connect");
}
