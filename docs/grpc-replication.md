# gRPC Replication

This document describes the gRPC-based replication feature that allows TrogonEventStore clusters to use gRPC instead of TCP for internal data replication between nodes.

## Overview

TrogonEventStore now supports gRPC for internal cluster replication as an alternative to the traditional TCP-based replication. This provides several benefits:

- **Unified Protocol**: All cluster communication (gossip, elections, and replication) uses the same gRPC protocol
- **Better Debugging**: gRPC provides structured message formats and better observability
- **HTTP/2 Benefits**: Multiplexing, flow control, and improved connection management
- **Future Extensibility**: Easier to add new replication features using gRPC

## Configuration

To enable gRPC replication, set the `EnableGrpcReplication` option to `true`:

### Command Line
```bash
--cluster-enable-grpc-replication=true
```

### Environment Variable
```bash
EVENTSTORE_CLUSTER_ENABLE_GRPC_REPLICATION=true
```

### Configuration File
```yaml
Cluster:
  EnableGrpcReplication: true
```

## How It Works

### Architecture

When gRPC replication is enabled:

1. **Leader-side**: The `Replication` gRPC service handles incoming replication requests from replicas
2. **Replica-side**: The `GrpcReplicaService` establishes bidirectional streaming connections to the leader
3. **Protocol**: Uses the same message types as TCP replication but transported over gRPC streams

### Message Flow

1. **Subscription**: Replica sends `SubscribeReplica` request to leader
2. **Confirmation**: Leader responds with `ReplicaSubscribed` containing starting position
3. **Data Streaming**: Leader continuously streams `DataChunkBulk` and `RawChunkBulk` messages
4. **Acknowledgments**: Replica sends `ReplicaLogPositionAck` messages to confirm receipt
5. **Role Assignment**: Leader sends `FollowerAssignment` or `CloneAssignment` based on performance

### Service Components

- **Cluster.Replication**: gRPC service implementation for handling replication requests
- **GrpcReplicaService**: Client-side service for connecting to leader and handling replication
- **Protobuf Definitions**: Complete message definitions in `src/Protos/Grpc/cluster.proto`

## Migration from TCP

### Gradual Migration

gRPC replication can be enabled on a per-node basis, allowing gradual migration:

1. **Phase 1**: Enable gRPC replication on followers first
2. **Phase 2**: Enable on the leader when all followers support gRPC
3. **Phase 3**: Verify all nodes are using gRPC successfully

### Rollback

If issues occur, gRPC replication can be disabled by setting `EnableGrpcReplication=false` and restarting the node. The node will fall back to TCP-based replication.

## Monitoring

### gRPC Metrics

gRPC replication provides enhanced observability through:

- **Connection Status**: Monitor active streaming connections
- **Message Throughput**: Track replication message rates
- **Error Rates**: Monitor gRPC status codes and errors
- **Latency**: Measure round-trip times for acknowledgments

### Health Checks

The `GetReplicationInfo` endpoint provides information about:
- Number of active subscriptions
- Replica endpoints and positions
- Connection states

## Troubleshooting

### Common Issues

**Connection Failures**
- Verify gRPC endpoints are accessible
- Check TLS configuration if using secure connections
- Ensure firewall rules allow gRPC traffic

**Performance Issues**
- Monitor gRPC flow control and backpressure
- Check for network congestion
- Verify chunk size and acknowledgment settings

**Compatibility Issues**
- Ensure all nodes support gRPC replication before enabling
- Check EventStore version compatibility

### Debugging

Enable detailed logging for gRPC replication:

```yaml
Logging:
  Level: Debug
  LoggerFilters:
    - "EventStore.Core.Services.Transport.Grpc.Cluster.Replication": Debug
    - "EventStore.Core.Services.Replication.GrpcReplicaService": Debug
```

## Performance Considerations

### Optimal Settings

gRPC replication maintains the same performance characteristics as TCP:

- **Chunk Size**: 8KB chunks for optimal throughput
- **Send Window**: 16MB maximum unacknowledged data
- **Acknowledgment Threshold**: 512KB before sending acks
- **Flow Control**: Automatic backpressure via gRPC streams

### Resource Usage

- **Memory**: Similar to TCP replication
- **CPU**: Slight increase due to HTTP/2 overhead
- **Network**: Comparable bandwidth usage with improved connection efficiency

## Implementation Details

### Key Files

- `src/Protos/Grpc/cluster.proto` - Protobuf service and message definitions
- `src/EventStore.Core/Services/Transport/Grpc/Cluster.Replication.cs` - Server-side gRPC service
- `src/EventStore.Core/Services/Replication/GrpcReplicaService.cs` - Client-side replication service
- `src/EventStore.Core/Configuration/ClusterVNodeOptions.cs` - Configuration options

### Design Principles

1. **Compatibility**: Maintains exact same replication semantics as TCP
2. **Performance**: Preserves all performance characteristics
3. **Reliability**: Implements equivalent error handling and recovery
4. **Observability**: Enhanced monitoring and debugging capabilities

## Future Enhancements

Planned improvements for gRPC replication:

- **Automatic Protocol Negotiation**: Detect best protocol automatically
- **Compression Options**: Configurable compression for bandwidth optimization
- **Advanced Flow Control**: More sophisticated backpressure mechanisms
- **Metrics Integration**: Enhanced integration with monitoring systems

## Support

For issues related to gRPC replication:

1. Check the troubleshooting section above
2. Enable debug logging for detailed diagnostics
3. Verify configuration and network connectivity
4. Report issues with detailed logs and configuration

---

**Note**: gRPC replication is available starting from TrogonEventStore version 24.x and requires .NET 8.0 or later.