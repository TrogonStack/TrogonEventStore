using EventStore.Plugins.Authorization;

namespace EventStore.Core.Authorization;

internal static class ReplicationOperations
{
	public static readonly OperationDefinition Connect = new("node/replication", "connect");
}
