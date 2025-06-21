using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Tasks;
using EventStore.Cluster;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Metrics;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Plugins.Authorization;
using Google.Protobuf;
using Grpc.Core;

namespace EventStore.Core.Services.Transport.Grpc.Cluster {
	partial class Replication {
		private readonly IPublisher _bus;
		private readonly IAuthorizationProvider _authorizationProvider;
		private readonly DurationTracker _streamReplicationTracker;
		private readonly string _clusterDns;
		
		// Track active streaming connections
		private readonly ConcurrentDictionary<string, IServerStreamWriter<ReplicationResponse>> _activeConnections 
			= new ConcurrentDictionary<string, IServerStreamWriter<ReplicationResponse>>();

		// Use existing gossip operation for now - replication is internal cluster communication
		private static readonly Operation ReplicationOperation = new Operation(Plugins.Authorization.Operations.Node.Gossip.Update);

		public Replication(IPublisher bus, IAuthorizationProvider authorizationProvider, QueueTrackers queueTrackers, string clusterDns) {
			_bus = bus ?? throw new ArgumentNullException(nameof(bus));
			_authorizationProvider = authorizationProvider ?? throw new ArgumentNullException(nameof(authorizationProvider));
			_clusterDns = clusterDns;
			_streamReplicationTracker = queueTrackers.GetTracker("grpc-stream-replication");
		}

		public override async Task<ReplicationInfo> GetReplicationInfo(EventStore.Client.Empty request, ServerCallContext context) {
			var user = context.GetHttpContext().User;
			if (!await _authorizationProvider.CheckAccessAsync(user, ReplicationOperation, context.CancellationToken)) {
				throw RpcExceptions.AccessDenied();
			}

			// Get replication stats from internal services
			var envelope = new TcsEnvelope<ReplicationMessage.GetReplicationStatsCompleted>();
			_bus.Publish(new ReplicationMessage.GetReplicationStats(envelope));
			
			var response = await envelope.Task;
			
			var replicationInfo = new ReplicationInfo {
				ActiveSubscriptions = response.ReplicationStats?.Count ?? 0
			};

			if (response.ReplicationStats != null) {
				foreach (var stat in response.ReplicationStats) {
					replicationInfo.Subscriptions.Add(new ReplicationSubscriptionInfo {
						SubscriptionId = ByteString.CopyFrom(stat.SubscriptionId.ToByteArray()),
						ReplicaEndpoint = new EventStore.Cluster.EndPoint {
							Address = stat.SubscriptionEndpoint,
							Port = 0 // We don't have port info in stats
						},
						SubscriptionPosition = stat.TotalBytesSent,
						State = "Active"
					});
				}
			}

			return replicationInfo;
		}

		public override async Task StreamReplication(
			IAsyncStreamReader<ReplicationRequest> requestStream,
			IServerStreamWriter<ReplicationResponse> responseStream,
			ServerCallContext context) {
			
			var user = context.GetHttpContext().User;
			if (!await _authorizationProvider.CheckAccessAsync(user, ReplicationOperation, context.CancellationToken)) {
				throw RpcExceptions.AccessDenied();
			}

			using var activity = _streamReplicationTracker.StartActivity();
			
			string connectionId = null;
			
			try {
				// Handle incoming requests from replica
				await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken)) {
					switch (request.RequestCase) {
						case ReplicationRequest.RequestOneofCase.SubscribeReplica:
							connectionId = await HandleSubscribeReplica(request.SubscribeReplica, responseStream, context);
							break;
							
						case ReplicationRequest.RequestOneofCase.ReplicaLogPositionAck:
							HandleReplicaLogPositionAck(request.ReplicaLogPositionAck);
							break;
							
						case ReplicationRequest.RequestOneofCase.DropSubscription:
							HandleDropSubscription(request.DropSubscription);
							if (connectionId != null) {
								_activeConnections.TryRemove(connectionId, out _);
							}
							return;
							
						case ReplicationRequest.RequestOneofCase.ReplicaSubscriptionRetry:
							HandleReplicaSubscriptionRetry(request.ReplicaSubscriptionRetry);
							break;
					}
				}
			} catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) {
				// Client disconnected, clean up
				if (connectionId != null) {
					_activeConnections.TryRemove(connectionId, out _);
				}
			} catch (Exception ex) {
				// Log error and clean up
				if (connectionId != null) {
					_activeConnections.TryRemove(connectionId, out _);
				}
				throw;
			} finally {
				if (connectionId != null) {
					_activeConnections.TryRemove(connectionId, out _);
				}
			}
		}

		private async Task<string> HandleSubscribeReplica(
			EventStore.Cluster.SubscribeReplica request, 
			IServerStreamWriter<ReplicationResponse> responseStream,
			ServerCallContext context) {
			
			var connectionId = Guid.NewGuid().ToString();
			_activeConnections.TryAdd(connectionId, responseStream);

			// Convert gRPC request to internal message
			var subscriptionId = new Guid(request.SubscriptionId.ToByteArray());
			var leaderId = new Guid(request.LeaderId.ToByteArray());
			var chunkId = new Guid(request.ChunkId.ToByteArray());
			
			// Convert epochs
			var epochs = new Data.Epoch[request.LastEpochs.Count];
			for (int i = 0; i < request.LastEpochs.Count; i++) {
				var epoch = request.LastEpochs[i];
				epochs[i] = new Data.Epoch(
					new Guid(epoch.EpochId.ToByteArray()),
					epoch.EpochNumber,
					epoch.EpochPosition);
			}

			// Create endpoint from IP and port
			EndPoint replicaEndPoint;
			try {
				var ipBytes = request.Ip.ToByteArray();
				var ipAddress = new IPAddress(ipBytes);
				replicaEndPoint = new IPEndPoint(ipAddress, request.Port);
			} catch {
				// Fallback to DNS endpoint if IP parsing fails
				var ipString = System.Text.Encoding.UTF8.GetString(request.Ip.ToByteArray());
				replicaEndPoint = new DnsEndPoint(ipString, request.Port).WithClusterDns(_clusterDns);
			}

			// Create internal subscription request message
			var internalMessage = new ReplicationMessage.ReplicaSubscriptionRequest(
				correlationId: Guid.NewGuid(),
				envelope: new GrpcReplicationEnvelope(connectionId, responseStream, this),
				connection: null, // We don't have TCP connection in gRPC
				version: request.Version,
				logPosition: request.LogPosition,
				chunkId: chunkId,
				lastEpochs: epochs,
				replicaEndPoint: replicaEndPoint,
				leaderId: leaderId,
				subscriptionId: subscriptionId,
				isPromotable: request.IsPromotable);

			_bus.Publish(internalMessage);
			
			return connectionId;
		}

		private void HandleReplicaLogPositionAck(EventStore.Cluster.ReplicaLogPositionAck request) {
			var subscriptionId = new Guid(request.SubscriptionId.ToByteArray());
			
			var internalMessage = new ReplicationMessage.ReplicaLogPositionAck(
				subscriptionId: subscriptionId,
				replicationLogPosition: request.ReplicationLogPosition,
				writerLogPosition: request.WriterLogPosition);

			_bus.Publish(internalMessage);
		}

		private void HandleDropSubscription(EventStore.Cluster.DropSubscription request) {
			var leaderId = new Guid(request.LeaderId.ToByteArray());
			var subscriptionId = new Guid(request.SubscriptionId.ToByteArray());
			
			var internalMessage = new ReplicationMessage.DropSubscription(
				leaderId: leaderId,
				subscriptionId: subscriptionId);

			_bus.Publish(internalMessage);
		}

		private void HandleReplicaSubscriptionRetry(EventStore.Cluster.ReplicaSubscriptionRetry request) {
			var leaderId = new Guid(request.LeaderId.ToByteArray());
			var subscriptionId = new Guid(request.SubscriptionId.ToByteArray());
			
			var internalMessage = new ReplicationMessage.ReplicaSubscriptionRetry(
				leaderId: leaderId,
				subscriptionId: subscriptionId);

			_bus.Publish(internalMessage);
		}

		// Method to send responses to replicas (called by internal replication services)
		public async Task SendToReplica(string connectionId, ReplicationResponse response) {
			if (_activeConnections.TryGetValue(connectionId, out var responseStream)) {
				try {
					await responseStream.WriteAsync(response);
				} catch (Exception ex) {
					// Connection failed, remove it
					_activeConnections.TryRemove(connectionId, out _);
					throw;
				}
			}
		}
	}

	// Custom envelope to bridge gRPC responses back to internal message handlers
	public class GrpcReplicationEnvelope : IEnvelope {
		private readonly string _connectionId;
		private readonly IServerStreamWriter<ReplicationResponse> _responseStream;
		private readonly Replication _replicationService;

		public GrpcReplicationEnvelope(string connectionId, IServerStreamWriter<ReplicationResponse> responseStream, Replication replicationService) {
			_connectionId = connectionId;
			_responseStream = responseStream;
			_replicationService = replicationService;
		}

		public void ReplyWith<T>(T message) where T : Message {
			_ = Task.Run(async () => {
				try {
					var grpcResponse = ConvertToGrpcResponse(message);
					if (grpcResponse != null) {
						await _replicationService.SendToReplica(_connectionId, grpcResponse);
					}
				} catch (Exception ex) {
					// Log error - connection may have been closed
					// TODO: Add proper logging
				}
			});
		}

		private ReplicationResponse ConvertToGrpcResponse(Message message) {
			return message switch {
				ReplicationMessage.ReplicaSubscribed subscribed => new ReplicationResponse {
					ReplicaSubscribed = new EventStore.Cluster.ReplicaSubscribed {
						LeaderId = ByteString.CopyFrom(subscribed.LeaderId.ToByteArray()),
						SubscriptionId = ByteString.CopyFrom(subscribed.SubscriptionId.ToByteArray()),
						SubscriptionPosition = subscribed.SubscriptionPosition
					}
				},
				ReplicationMessage.DataChunkBulk dataChunk => new ReplicationResponse {
					DataChunkBulk = new EventStore.Cluster.DataChunkBulk {
						LeaderId = ByteString.CopyFrom(dataChunk.LeaderId.ToByteArray()),
						SubscriptionId = ByteString.CopyFrom(dataChunk.SubscriptionId.ToByteArray()),
						ChunkStartNumber = dataChunk.ChunkStartNumber,
						ChunkEndNumber = dataChunk.ChunkEndNumber,
						SubscriptionPosition = dataChunk.SubscriptionPosition,
						DataBytes = ByteString.CopyFrom(dataChunk.DataBytes),
						CompleteChunk = dataChunk.CompleteChunk
					}
				},
				ReplicationMessage.RawChunkBulk rawChunk => new ReplicationResponse {
					RawChunkBulk = new EventStore.Cluster.RawChunkBulk {
						LeaderId = ByteString.CopyFrom(rawChunk.LeaderId.ToByteArray()),
						SubscriptionId = ByteString.CopyFrom(rawChunk.SubscriptionId.ToByteArray()),
						ChunkStartNumber = rawChunk.ChunkStartNumber,
						ChunkEndNumber = rawChunk.ChunkEndNumber,
						RawPosition = rawChunk.RawPosition,
						RawBytes = ByteString.CopyFrom(rawChunk.RawBytes),
						CompleteChunk = rawChunk.CompleteChunk
					}
				},
				ReplicationMessage.CreateChunk createChunk => new ReplicationResponse {
					CreateChunk = new EventStore.Cluster.CreateChunk {
						LeaderId = ByteString.CopyFrom(createChunk.LeaderId.ToByteArray()),
						SubscriptionId = ByteString.CopyFrom(createChunk.SubscriptionId.ToByteArray()),
						ChunkHeaderBytes = ByteString.CopyFrom(createChunk.ChunkHeader.AsByteArray()),
						FileSize = createChunk.FileSize,
						IsScavengedChunk = createChunk.IsScavengedChunk,
						TransformHeaderBytes = ByteString.CopyFrom(createChunk.TransformHeader.Span)
					}
				},
				ReplicationMessage.FollowerAssignment follower => new ReplicationResponse {
					FollowerAssignment = new EventStore.Cluster.FollowerAssignment {
						LeaderId = ByteString.CopyFrom(follower.LeaderId.ToByteArray()),
						SubscriptionId = ByteString.CopyFrom(follower.SubscriptionId.ToByteArray())
					}
				},
				ReplicationMessage.CloneAssignment clone => new ReplicationResponse {
					CloneAssignment = new EventStore.Cluster.CloneAssignment {
						LeaderId = ByteString.CopyFrom(clone.LeaderId.ToByteArray()),
						SubscriptionId = ByteString.CopyFrom(clone.SubscriptionId.ToByteArray())
					}
				},
				_ => null
			};
		}
	}
}