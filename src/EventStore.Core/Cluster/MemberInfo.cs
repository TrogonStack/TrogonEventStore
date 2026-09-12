using System;
using System.Net;
using EventStore.Common.Utils;
using EventStore.Core.Data;
using EventStore.Core.TransactionLog.LogRecords;

namespace EventStore.Core.Cluster
{
	public class MemberInfo : IEquatable<MemberInfo>
	{
		public readonly Guid InstanceId;

		public readonly DateTime TimeStamp;
		public readonly VNodeState State;
		public readonly bool IsAlive;

		public readonly EndPoint ReplicationEndPoint;
		public readonly EndPoint HttpEndPoint;
		public readonly string AdvertiseHostToClientAs;
		public readonly int AdvertiseHttpPortToClientAs;

		public readonly long LastCommitPosition;
		public readonly long WriterCheckpoint;
		public readonly long ChaserCheckpoint;
		public readonly long EpochPosition;
		public readonly int EpochNumber;
		public readonly Guid EpochId;

		public readonly int NodePriority;
		public readonly bool IsReadOnlyReplica;

		public readonly string ESVersion;

		public static MemberInfo ForManager(Guid instanceId, DateTime timeStamp, bool isAlive,
			EndPoint httpEndPoint, string esVersion = VersionInfo.UnknownVersion,
			EndPoint replicationEndPoint = null)
		{
			return new MemberInfo(instanceId, timeStamp, VNodeState.Manager, isAlive,
				httpEndPoint, null, 0,
				-1, -1, -1, -1, -1, Guid.Empty, 0, false, esVersion, replicationEndPoint);
		}

		public static MemberInfo ForVNode(Guid instanceId,
			DateTime timeStamp,
			VNodeState state,
			bool isAlive,
			EndPoint httpEndPoint,
			string advertiseHostToClientAs,
			int advertiseHttpPortToClientAs,
			long lastCommitPosition,
			long writerCheckpoint,
			long chaserCheckpoint,
			long epochPosition,
			int epochNumber,
			Guid epochId,
			int nodePriority,
			bool isReadOnlyReplica, string esVersion = VersionInfo.UnknownVersion,
			EndPoint replicationEndPoint = null)
		{
			if (state == VNodeState.Manager)
			{
				throw new ArgumentException(string.Format("Wrong State for VNode: {0}", state), "state");
			}

			return new MemberInfo(instanceId, timeStamp, state, isAlive,
				httpEndPoint, advertiseHostToClientAs, advertiseHttpPortToClientAs,
				lastCommitPosition, writerCheckpoint, chaserCheckpoint,
				epochPosition, epochNumber, epochId, nodePriority, isReadOnlyReplica, esVersion,
				replicationEndPoint);
		}

		public static MemberInfo Initial(Guid instanceId,
			DateTime timeStamp,
			VNodeState state,
			bool isAlive,
			EndPoint httpEndPoint,
			string advertiseHostToClientAs,
			int advertiseHttpPortToClientAs,
			int nodePriority,
			bool isReadOnlyReplica, string esVersion = VersionInfo.UnknownVersion,
			EndPoint replicationEndPoint = null)
		{
			if (state == VNodeState.Manager)
			{
				throw new ArgumentException(string.Format("Wrong State for VNode: {0}", state), "state");
			}

			return new MemberInfo(instanceId, timeStamp, state, isAlive,
				httpEndPoint, advertiseHostToClientAs, advertiseHttpPortToClientAs,
				-1, -1, -1, -1, -1, Guid.Empty, nodePriority, isReadOnlyReplica, esVersion,
				replicationEndPoint);
		}

		internal MemberInfo(Guid instanceId, DateTime timeStamp, VNodeState state, bool isAlive,
			EndPoint httpEndPoint, string advertiseHostToClientAs, int advertiseHttpPortToClientAs,
			long lastCommitPosition, long writerCheckpoint, long chaserCheckpoint,
			long epochPosition, int epochNumber, Guid epochId, int nodePriority, bool isReadOnlyReplica,
			string esVersion = null, EndPoint replicationEndPoint = null)
		{
			Ensure.NotNull(httpEndPoint, nameof(httpEndPoint));

			InstanceId = instanceId;

			TimeStamp = timeStamp;
			State = state;
			IsAlive = isAlive;

			ReplicationEndPoint = replicationEndPoint ?? httpEndPoint;
			HttpEndPoint = httpEndPoint;
			AdvertiseHostToClientAs = advertiseHostToClientAs;
			AdvertiseHttpPortToClientAs = advertiseHttpPortToClientAs;

			LastCommitPosition = lastCommitPosition;
			WriterCheckpoint = writerCheckpoint;
			ChaserCheckpoint = chaserCheckpoint;

			EpochPosition = epochPosition;
			EpochNumber = epochNumber;
			EpochId = epochId;

			NodePriority = nodePriority;
			IsReadOnlyReplica = isReadOnlyReplica;

			ESVersion = esVersion;
		}

		public bool Is(EndPoint endPoint)
		{
			return endPoint != null &&
				(HttpEndPoint.EndPointEquals(endPoint) || ReplicationEndPoint.EndPointEquals(endPoint));
		}

		public MemberInfo Updated(DateTime utcNow,
			VNodeState? state = null,
			bool? isAlive = null,
			long? lastCommitPosition = null,
			long? writerCheckpoint = null,
			long? chaserCheckpoint = null,
			EpochRecord epoch = null,
			int? nodePriority = null, string esVersion = null)
		{
			return new MemberInfo(InstanceId,
				utcNow,
				state ?? State,
				isAlive ?? IsAlive,
				HttpEndPoint,
				AdvertiseHostToClientAs,
				AdvertiseHttpPortToClientAs,
				lastCommitPosition ?? LastCommitPosition,
				writerCheckpoint ?? WriterCheckpoint,
				chaserCheckpoint ?? ChaserCheckpoint,
				epoch != null ? epoch.EpochPosition : EpochPosition,
				epoch != null ? epoch.EpochNumber : EpochNumber,
				epoch != null ? epoch.EpochId : EpochId,
				nodePriority ?? NodePriority,
				IsReadOnlyReplica, esVersion ?? ESVersion, ReplicationEndPoint);
		}

		public override string ToString()
		{
			if (State == VNodeState.Manager)
			{
				return
					$"MAN {InstanceId:B} <{(IsAlive ? "LIVE" : "DEAD")}> [{State}, {ReplicationEndPoint}, {HttpEndPoint}] | {TimeStamp:yyyy-MM-dd HH:mm:ss.fff}";
			}

			return
				$"Priority: {NodePriority} VND {InstanceId:B} <{(IsAlive ? "LIVE" : "DEAD")}> [{State}, " +
				$"Replication:{ReplicationEndPoint}, {HttpEndPoint}, (ADVERTISED: HTTP:{AdvertiseHostToClientAs}:{AdvertiseHttpPortToClientAs}), " +
				$"Version: {ESVersion}] " +
				$"{LastCommitPosition}/{WriterCheckpoint}/{ChaserCheckpoint}/E{EpochNumber}@{EpochPosition}:{EpochId:B} | {TimeStamp:yyyy-MM-dd HH:mm:ss.fff}";
		}

		public bool Equals(MemberInfo other)
		{
			// we ignore timestamp and checkpoints for equality comparison
			if (ReferenceEquals(null, other))
			{
				return false;
			}

			if (ReferenceEquals(this, other))
			{
				return true;
			}

			return other.InstanceId == InstanceId
				   && other.State == State
				   && other.IsAlive == IsAlive
				   && Equals(other.ReplicationEndPoint, ReplicationEndPoint)
				   && Equals(other.HttpEndPoint, HttpEndPoint)
				   && other.AdvertiseHostToClientAs == AdvertiseHostToClientAs
				   && other.AdvertiseHttpPortToClientAs == AdvertiseHttpPortToClientAs
				   && other.EpochPosition == EpochPosition
				   && other.EpochNumber == EpochNumber
				   && other.EpochId == EpochId
				   && other.NodePriority == NodePriority
				   && other.IsReadOnlyReplica == IsReadOnlyReplica
				   && other.ESVersion == ESVersion;
		}

		public override bool Equals(object obj)
		{
			if (ReferenceEquals(null, obj))
			{
				return false;
			}

			if (ReferenceEquals(this, obj))
			{
				return true;
			}

			if (obj.GetType() != typeof(MemberInfo))
			{
				return false;
			}

			return Equals((MemberInfo)obj);
		}

		public override int GetHashCode()
		{
			unchecked
			{
				int result = InstanceId.GetHashCode();
				result = (result * 397) ^ State.GetHashCode();
				result = (result * 397) ^ IsAlive.GetHashCode();
				result = (result * 397) ^ ReplicationEndPoint.GetHashCode();
				result = (result * 397) ^ HttpEndPoint.GetHashCode();
				result = (result * 397) ^ (AdvertiseHostToClientAs != null ? AdvertiseHostToClientAs.GetHashCode() : 0);
				result = (result * 397) ^ AdvertiseHttpPortToClientAs.GetHashCode();
				result = (result * 397) ^ EpochPosition.GetHashCode();
				result = (result * 397) ^ EpochNumber.GetHashCode();
				result = (result * 397) ^ EpochId.GetHashCode();
				result = (result * 397) ^ NodePriority;
				result = (result * 397) ^ (ESVersion != null ? ESVersion.GetHashCode() : 0);
				return result;
			}
		}
	}
}
