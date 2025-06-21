using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using EventStore.Cluster;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Storage.EpochManager;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.LogRecords;
using Google.Protobuf;
using Grpc.Core;
using EndPoint = System.Net.EndPoint;
using ILogger = Serilog.ILogger;

namespace EventStore.Core.Services.Replication {
	public class GrpcReplicaService : 
		IHandle<SystemMessage.StateChangeMessage>,
		IHandle<ReplicationMessage.ReconnectToLeader>,
		IAsyncHandle<ReplicationMessage.SubscribeToLeader>,
		IHandle<ReplicationMessage.AckLogPosition> {
		
		private static readonly ILogger Log = Serilog.Log.ForContext<GrpcReplicaService>();

		private readonly IPublisher _publisher;
		private readonly TFChunkDb _db;
		private readonly IEpochManager _epochManager;
		private readonly EventStoreClusterClientCache _clientCache;
		private readonly EndPoint _internalEndPoint;
		private readonly bool _isReadOnlyReplica;
		private readonly string _clusterDns;

		private VNodeState _state = VNodeState.Initializing;
		private EventStore.Core.Cluster.MemberInfo _currentLeader;
		private EventStore.Cluster.Replication.ReplicationClient _currentClient;
		private AsyncDuplexStreamingCall<ReplicationRequest, ReplicationResponse> _streamingCall;
		private CancellationTokenSource _connectionCancellation;
		private Task _connectionTask;

		public GrpcReplicaService(
			IPublisher publisher,
			TFChunkDb db,
			IEpochManager epochManager,
			EventStoreClusterClientCache clientCache,
			EndPoint internalEndPoint,
			bool isReadOnlyReplica,
			string clusterDns) {
			
			Ensure.NotNull(publisher, nameof(publisher));
			Ensure.NotNull(db, nameof(db));
			Ensure.NotNull(epochManager, nameof(epochManager));
			Ensure.NotNull(clientCache, nameof(clientCache));
			Ensure.NotNull(internalEndPoint, nameof(internalEndPoint));

			_publisher = publisher;
			_db = db;
			_epochManager = epochManager;
			_clientCache = clientCache;
			_internalEndPoint = internalEndPoint;
			_isReadOnlyReplica = isReadOnlyReplica;
			_clusterDns = clusterDns;
		}

		public void Handle(SystemMessage.StateChangeMessage message) {
			_state = message.State;

			switch (message.State) {
				case VNodeState.Initializing:
				case VNodeState.DiscoverLeader:
				case VNodeState.Unknown:
				case VNodeState.ReadOnlyLeaderless:
				case VNodeState.PreLeader:
				case VNodeState.Leader:
				case VNodeState.ResigningLeader:
				case VNodeState.ShuttingDown:
				case VNodeState.Shutdown:
					Disconnect();
					break;
					
				case VNodeState.PreReplica: {
					var m = (SystemMessage.BecomePreReplica)message;
					ConnectToLeader(m.LeaderConnectionCorrelationId, m.Leader);
					break;
				}
				case VNodeState.PreReadOnlyReplica: {
					var m = (SystemMessage.BecomePreReadOnlyReplica)message;
					ConnectToLeader(m.LeaderConnectionCorrelationId, m.Leader);
					break;
				}
				case VNodeState.CatchingUp:
				case VNodeState.Clone:
				case VNodeState.Follower:
				case VNodeState.ReadOnlyReplica:
					// State change within replication states - connection should remain
					break;
					
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		public void Handle(ReplicationMessage.ReconnectToLeader message) {
			ConnectToLeader(message.ConnectionCorrelationId, message.Leader);
		}

		public ValueTask HandleAsync(ReplicationMessage.SubscribeToLeader message, CancellationToken token) {
			return new ValueTask(Handle(message));
		}

		public async Task Handle(ReplicationMessage.SubscribeToLeader message) {
			if (_streamingCall == null || _currentLeader?.InstanceId != message.LeaderId) {
				Log.Warning("Cannot subscribe to leader - no active connection or leader mismatch");
				return;
			}

			try {
				// Send subscription request
				var epochs = await GetLastEpochs();
				var subscribeRequest = CreateSubscribeRequest(message.SubscriptionId, message.LeaderId, epochs);
				
				await _streamingCall.RequestStream.WriteAsync(subscribeRequest);
			} catch (Exception ex) {
				Log.Error(ex, "Failed to send subscription request to leader");
				_publisher.Publish(new ReplicationMessage.LeaderConnectionFailed(
					message.StateCorrelationId, _currentLeader));
			}
		}

		public void Handle(ReplicationMessage.AckLogPosition message) {
			if (_streamingCall == null) {
				return;
			}

			try {
				var ackRequest = new ReplicationRequest {
					ReplicaLogPositionAck = new ReplicaLogPositionAck {
						SubscriptionId = ByteString.CopyFrom(message.SubscriptionId.ToByteArray()),
						ReplicationLogPosition = message.ReplicationLogPosition,
						WriterLogPosition = message.WriterLogPosition
					}
				};

				// Fire and forget - don't await to avoid blocking
				_ = Task.Run(async () => {
					try {
						await _streamingCall.RequestStream.WriteAsync(ackRequest);
					} catch (Exception ex) {
						Log.Warning(ex, "Failed to send ack to leader");
					}
				});
			} catch (Exception ex) {
				Log.Warning(ex, "Failed to create ack request");
			}
		}

		private void ConnectToLeader(Guid leaderConnectionCorrelationId, EventStore.Core.Cluster.MemberInfo leader) {
			if (_state != VNodeState.PreReplica && _state != VNodeState.PreReadOnlyReplica) {
				return;
			}

			Disconnect();

			_currentLeader = leader;
			_connectionCancellation = new CancellationTokenSource();
			_connectionTask = ConnectToLeaderAsync(leaderConnectionCorrelationId, leader, _connectionCancellation.Token);
		}

		private async Task ConnectToLeaderAsync(Guid leaderConnectionCorrelationId, EventStore.Core.Cluster.MemberInfo leader, CancellationToken cancellationToken) {
			try {
				Log.Information("Connecting to leader {leaderId} at {endpoint}", leader.InstanceId, leader.HttpEndPoint);

				var client = _clientCache.Get(leader.HttpEndPoint);
				_currentClient = new EventStore.Cluster.Replication.ReplicationClient(client.Channel);

				_streamingCall = _currentClient.StreamReplication(cancellationToken: cancellationToken);

				// Notify connection established
				_publisher.Publish(new SystemMessage.VNodeConnectionEstablished(
					leader.HttpEndPoint, leaderConnectionCorrelationId));

				// Start reading responses
				await HandleIncomingMessages(cancellationToken);
			} catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) {
				Log.Information("Connection to leader cancelled");
			} catch (Exception ex) {
				Log.Error(ex, "Failed to connect to leader {leaderId}", leader.InstanceId);
				_publisher.Publish(new ReplicationMessage.LeaderConnectionFailed(
					leaderConnectionCorrelationId, leader));
			}
		}

		private async Task HandleIncomingMessages(CancellationToken cancellationToken) {
			try {
				await foreach (var response in _streamingCall.ResponseStream.ReadAllAsync(cancellationToken)) {
					var internalMessage = ConvertFromGrpcResponse(response);
					if (internalMessage != null) {
						_publisher.Publish(internalMessage);
					}
				}
			} catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) {
				// Expected when cancelling
			} catch (Exception ex) {
				Log.Error(ex, "Error reading from replication stream");
				if (_currentLeader != null) {
					_publisher.Publish(new SystemMessage.VNodeConnectionLost(
						_currentLeader.HttpEndPoint, Guid.NewGuid()));
				}
			}
		}

		private Message ConvertFromGrpcResponse(ReplicationResponse response) {
			return response.ResponseCase switch {
				ReplicationResponse.ResponseOneofCase.ReplicaSubscribed => new ReplicationMessage.ReplicaSubscribed(
					leaderId: new Guid(response.ReplicaSubscribed.LeaderId.ToByteArray()),
					subscriptionId: new Guid(response.ReplicaSubscribed.SubscriptionId.ToByteArray()),
					subscriptionPosition: response.ReplicaSubscribed.SubscriptionPosition),

				ReplicationResponse.ResponseOneofCase.DataChunkBulk => new ReplicationMessage.DataChunkBulk(
					leaderId: new Guid(response.DataChunkBulk.LeaderId.ToByteArray()),
					subscriptionId: new Guid(response.DataChunkBulk.SubscriptionId.ToByteArray()),
					chunkStartNumber: response.DataChunkBulk.ChunkStartNumber,
					chunkEndNumber: response.DataChunkBulk.ChunkEndNumber,
					subscriptionPosition: response.DataChunkBulk.SubscriptionPosition,
					dataBytes: response.DataChunkBulk.DataBytes.ToByteArray(),
					completeChunk: response.DataChunkBulk.CompleteChunk),

				ReplicationResponse.ResponseOneofCase.RawChunkBulk => new ReplicationMessage.RawChunkBulk(
					leaderId: new Guid(response.RawChunkBulk.LeaderId.ToByteArray()),
					subscriptionId: new Guid(response.RawChunkBulk.SubscriptionId.ToByteArray()),
					chunkStartNumber: response.RawChunkBulk.ChunkStartNumber,
					chunkEndNumber: response.RawChunkBulk.ChunkEndNumber,
					rawPosition: response.RawChunkBulk.RawPosition,
					rawBytes: response.RawChunkBulk.RawBytes.ToByteArray(),
					completeChunk: response.RawChunkBulk.CompleteChunk),

				ReplicationResponse.ResponseOneofCase.CreateChunk => ConvertCreateChunk(response.CreateChunk),

				ReplicationResponse.ResponseOneofCase.FollowerAssignment => new ReplicationMessage.FollowerAssignment(
					leaderId: new Guid(response.FollowerAssignment.LeaderId.ToByteArray()),
					subscriptionId: new Guid(response.FollowerAssignment.SubscriptionId.ToByteArray())),

				ReplicationResponse.ResponseOneofCase.CloneAssignment => new ReplicationMessage.CloneAssignment(
					leaderId: new Guid(response.CloneAssignment.LeaderId.ToByteArray()),
					subscriptionId: new Guid(response.CloneAssignment.SubscriptionId.ToByteArray())),

				_ => null
			};
		}

		private ReplicationMessage.CreateChunk ConvertCreateChunk(CreateChunk createChunk) {
			// Parse chunk header from bytes
			var chunkHeaderBytes = createChunk.ChunkHeaderBytes.ToByteArray();
			var chunkHeader = new TransactionLog.Chunks.ChunkHeader(chunkHeaderBytes);

			return new ReplicationMessage.CreateChunk(
				leaderId: new Guid(createChunk.LeaderId.ToByteArray()),
				subscriptionId: new Guid(createChunk.SubscriptionId.ToByteArray()),
				chunkHeader: chunkHeader,
				fileSize: createChunk.FileSize,
				isScavengedChunk: createChunk.IsScavengedChunk,
				transformHeader: createChunk.TransformHeaderBytes.ToByteArray());
		}

		private ReplicationRequest CreateSubscribeRequest(Guid subscriptionId, Guid leaderId, EventStore.Core.Data.Epoch[] epochs) {
			var epochProtos = new List<EventStore.Cluster.Epoch>();
			foreach (var epoch in epochs) {
				epochProtos.Add(new EventStore.Cluster.Epoch {
					EpochPosition = epoch.EpochPosition,
					EpochNumber = epoch.EpochNumber,
					EpochId = ByteString.CopyFrom(epoch.EpochId.ToByteArray())
				});
			}

			// Convert endpoint to IP bytes
			byte[] ipBytes;
			int port;
			if (_internalEndPoint is IPEndPoint ipEndPoint) {
				ipBytes = ipEndPoint.Address.GetAddressBytes();
				port = ipEndPoint.Port;
			} else if (_internalEndPoint is DnsEndPoint dnsEndPoint) {
				ipBytes = System.Text.Encoding.UTF8.GetBytes(dnsEndPoint.Host);
				port = dnsEndPoint.Port;
			} else {
				ipBytes = System.Text.Encoding.UTF8.GetBytes(_internalEndPoint.ToString());
				port = 0;
			}

			var logPosition = _db.Config.WriterCheckpoint.Read();
			var chunkId = Guid.Empty;
			
			// the chunk may not exist if it's a new database or if we're at a chunk boundary
			if (_db.Manager.TryGetChunkFor(logPosition, out var chunk))
				chunkId = chunk.ChunkHeader.ChunkId;

			return new ReplicationRequest {
				SubscribeReplica = new SubscribeReplica {
					LogPosition = logPosition,
					ChunkId = ByteString.CopyFrom(chunkId.ToByteArray()),
					LastEpochs = { epochProtos },
					Ip = ByteString.CopyFrom(ipBytes),
					Port = port,
					LeaderId = ByteString.CopyFrom(leaderId.ToByteArray()),
					SubscriptionId = ByteString.CopyFrom(subscriptionId.ToByteArray()),
					IsPromotable = !_isReadOnlyReplica,
					Version = 1
				}
			};
		}

		private async Task<EventStore.Core.Data.Epoch[]> GetLastEpochs() {
			// Get last epochs from epoch manager
			var epochRecords = await _epochManager.GetLastEpochs(10, CancellationToken.None); // Get last 10 epochs
			return epochRecords.Select(e => new EventStore.Core.Data.Epoch(e.EpochPosition, e.EpochNumber, e.EpochId)).ToArray();
		}

		private void Disconnect() {
			try {
				if (_connectionCancellation != null) {
					_connectionCancellation.Cancel();
					_connectionCancellation.Dispose();
					_connectionCancellation = null;
				}

				if (_streamingCall != null) {
					_streamingCall.Dispose();
					_streamingCall = null;
				}

				_currentClient = null;

				if (_currentLeader != null) {
					Log.Information("Disconnected from leader {leaderId}", _currentLeader.InstanceId);
					_publisher.Publish(new SystemMessage.VNodeConnectionLost(
						_currentLeader.HttpEndPoint, Guid.NewGuid()));
					_currentLeader = null;
				}
			} catch (Exception ex) {
				Log.Warning(ex, "Error during disconnect");
			}
		}
	}
}