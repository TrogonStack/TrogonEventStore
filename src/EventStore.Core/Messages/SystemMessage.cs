using System;
using EventStore.Core.Cluster;
using EventStore.Common.Utils;
using EventStore.Core.Data;
using EventStore.Core.Messaging;
using EventStore.Core.TransactionLog.LogRecords;
using EndPoint = System.Net.EndPoint;

namespace EventStore.Core.Messages;

public static partial class SystemMessage
{
	[DerivedMessage(CoreMessage.System)]
	public partial class SystemInit : Message;

	[DerivedMessage(CoreMessage.System)]
	public partial class SystemStart : Message;

	[DerivedMessage(CoreMessage.System)]
	public partial class SystemCoreReady : Message;

	[DerivedMessage(CoreMessage.System)]
	public partial class SystemReady : Message;

	[DerivedMessage(CoreMessage.System)]
	public partial class ServiceInitialized : Message
	{
		public readonly string ServiceName;

		public ServiceInitialized(string serviceName)
		{
			Ensure.NotNullOrEmpty(serviceName, "serviceName");
			ServiceName = serviceName;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class SubSystemInitialized : Message
	{
		public readonly string SubSystemName;

		public SubSystemInitialized(string subSystemName)
		{
			Ensure.NotNullOrEmpty(subSystemName, "subSystemName");
			SubSystemName = subSystemName;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class WriteEpoch(int epochNumber) : Message
	{
		public readonly int EpochNumber = epochNumber;
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class InitiateLeaderResignation : Message;

	[DerivedMessage(CoreMessage.System)]
	public partial class RequestQueueDrained : Message;

	[DerivedMessage]
	public abstract partial class StateChangeMessage : Message
	{
		public readonly Guid CorrelationId;
		public readonly VNodeState State;

		protected StateChangeMessage(Guid correlationId, VNodeState state)
		{
			Ensure.NotEmptyGuid(correlationId, "correlationId");
			CorrelationId = correlationId;
			State = state;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomePreLeader(Guid correlationId) : StateChangeMessage(correlationId, VNodeState.PreLeader);

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomeLeader(Guid correlationId) : StateChangeMessage(correlationId, VNodeState.Leader);

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomeShuttingDown : StateChangeMessage
	{
		public readonly bool ShutdownHttp;
		public readonly bool ExitProcess;

		public BecomeShuttingDown(Guid correlationId, bool exitProcess, bool shutdownHttp) : base(correlationId,
			VNodeState.ShuttingDown)
		{
			ShutdownHttp = shutdownHttp;
			Ensure.NotEmptyGuid(correlationId, "correlationId");
			ExitProcess = exitProcess;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomeShutdown(Guid correlationId) : StateChangeMessage(correlationId, VNodeState.Shutdown);

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomeUnknown(Guid correlationId) : StateChangeMessage(correlationId, VNodeState.Unknown);

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomeDiscoverLeader(Guid correlationId)
		: StateChangeMessage(correlationId, VNodeState.DiscoverLeader);

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomeResigningLeader(Guid correlationId)
		: StateChangeMessage(correlationId, VNodeState.ResigningLeader);

	[DerivedMessage]
	public abstract partial class ReplicaStateMessage : StateChangeMessage
	{
		public readonly MemberInfo Leader;

		protected ReplicaStateMessage(Guid correlationId, VNodeState state, MemberInfo leader)
			: base(correlationId, state)
		{
			Ensure.NotNull(leader, "leader");
			Leader = leader;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomePreReplica(Guid correlationId, Guid leaderConnectionCorrelationId, MemberInfo leader)
		: ReplicaStateMessage(correlationId, VNodeState.PreReplica, leader)
	{
		public readonly Guid LeaderConnectionCorrelationId = leaderConnectionCorrelationId;
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomeCatchingUp(Guid correlationId, MemberInfo leader) : ReplicaStateMessage(correlationId,
		VNodeState.CatchingUp,
		leader);

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomeClone(Guid correlationId, MemberInfo leader)
		: ReplicaStateMessage(correlationId, VNodeState.Clone, leader);

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomeFollower(Guid correlationId, MemberInfo leader) : ReplicaStateMessage(correlationId,
		VNodeState.Follower,
		leader);

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomeReadOnlyLeaderless(Guid correlationId)
		: StateChangeMessage(correlationId, VNodeState.ReadOnlyLeaderless);

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomePreReadOnlyReplica(
		Guid correlationId,
		Guid leaderConnectionCorrelationId,
		MemberInfo leader)
		: ReplicaStateMessage(correlationId, VNodeState.PreReadOnlyReplica, leader)
	{
		public readonly Guid LeaderConnectionCorrelationId = leaderConnectionCorrelationId;
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class BecomeReadOnlyReplica(Guid correlationId, MemberInfo leader)
		: ReplicaStateMessage(correlationId, VNodeState.ReadOnlyReplica, leader);


	[DerivedMessage(CoreMessage.System)]
	public partial class ServiceShutdown : Message
	{
		public readonly string ServiceName;

		public ServiceShutdown(string serviceName)
		{
			if (string.IsNullOrEmpty(serviceName))
				throw new ArgumentNullException("serviceName");
			ServiceName = serviceName;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class PeripheralShutdownTimeout : Message;

	[DerivedMessage(CoreMessage.System)]
	public partial class ShutdownTimeout : Message;

	[DerivedMessage(CoreMessage.System)]
	public partial class RegisterForGracefulTermination(string componentName, Action action) : Message
	{
		public readonly string ComponentName = componentName;
		public readonly Action Action = action;
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class ComponentTerminated(string componentName) : Message
	{
		public readonly string ComponentName = componentName;
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class VNodeConnectionLost : Message
	{
		public readonly EndPoint VNodeEndPoint;
		public readonly Guid ConnectionId;
		public readonly Guid? SubscriptionId;

		public VNodeConnectionLost(EndPoint vNodeEndPoint, Guid connectionId, Guid? subscriptionId = null)
		{
			Ensure.NotNull(vNodeEndPoint, "vNodeEndPoint");
			Ensure.NotEmptyGuid(connectionId, "connectionId");

			VNodeEndPoint = vNodeEndPoint;
			ConnectionId = connectionId;
			SubscriptionId = subscriptionId;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class VNodeConnectionEstablished : Message
	{
		public readonly EndPoint VNodeEndPoint;
		public readonly Guid ConnectionId;

		public VNodeConnectionEstablished(EndPoint vNodeEndPoint, Guid connectionId)
		{
			Ensure.NotNull(vNodeEndPoint, "vNodeEndPoint");
			Ensure.NotEmptyGuid(connectionId, "connectionId");

			VNodeEndPoint = vNodeEndPoint;
			ConnectionId = connectionId;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class WaitForChaserToCatchUp : Message
	{
		public readonly Guid CorrelationId;
		public readonly TimeSpan TotalTimeWasted;

		public WaitForChaserToCatchUp(Guid correlationId, TimeSpan totalTimeWasted)
		{
			Ensure.NotEmptyGuid(correlationId, "correlationId");

			CorrelationId = correlationId;
			TotalTimeWasted = totalTimeWasted;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class ChaserCaughtUp : Message
	{
		public readonly Guid CorrelationId;

		public ChaserCaughtUp(Guid correlationId)
		{
			Ensure.NotEmptyGuid(correlationId, "correlationId");
			CorrelationId = correlationId;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class EnablePreLeaderReplication : Message;

	[DerivedMessage(CoreMessage.System)]
	public partial class CheckInaugurationConditions : Message;

	[DerivedMessage(CoreMessage.System)]
	public partial class RequestForwardingTimerTick : Message;

	[DerivedMessage(CoreMessage.System)]
	public partial class NoQuorumMessage : Message;

	[DerivedMessage(CoreMessage.System)]
	public partial class EpochWritten : Message
	{
		public readonly EpochRecord Epoch;

		public EpochWritten(EpochRecord epoch)
		{
			Ensure.NotNull(epoch, "epoch");
			Epoch = epoch;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class ChunkLoaded : Message
	{
		public readonly ChunkInfo ChunkInfo;

		public ChunkLoaded(ChunkInfo chunkInfo)
		{
			Ensure.NotNull(chunkInfo, nameof(chunkInfo));
			ChunkInfo = chunkInfo;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class ChunkCompleted : Message
	{
		public readonly ChunkInfo ChunkInfo;

		public ChunkCompleted(ChunkInfo chunkInfo)
		{
			Ensure.NotNull(chunkInfo, nameof(chunkInfo));
			ChunkInfo = chunkInfo;
		}
	}

	[DerivedMessage(CoreMessage.System)]
	public partial class ChunkSwitched : Message
	{
		public readonly ChunkInfo ChunkInfo;

		public ChunkSwitched(ChunkInfo chunkInfo)
		{
			Ensure.NotNull(chunkInfo, nameof(chunkInfo));
			ChunkInfo = chunkInfo;
		}
	}
}
