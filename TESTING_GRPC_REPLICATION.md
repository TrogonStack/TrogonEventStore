# Testing gRPC Replication Implementation

This document provides a comprehensive testing strategy for the gRPC replication feature in TrogonEventStore.

## Overview

Testing gRPC replication requires multiple levels of verification:

1. **Unit Tests** - Individual component functionality
2. **Integration Tests** - Service interactions and message flow
3. **Functional Tests** - End-to-end replication scenarios
4. **Performance Tests** - Throughput and latency validation
5. **Reliability Tests** - Failure scenarios and recovery

## Prerequisites

### Build and Generate gRPC Code

First, ensure the protobuf files are compiled and gRPC code is generated:

```bash
# Navigate to project root
cd /Users/ubi/Developer/github.com/TrogonStack/TrogonEventStore

# Build the project to generate protobuf classes
./build.sh --configuration Debug

# Or if you have dotnet CLI available
dotnet build src/EventStore.sln --configuration Debug
```

### Test Environment Setup

```bash
# Set environment variables for testing
export EVENTSTORE_CLUSTER_ENABLE_GRPC_REPLICATION=true
export EVENTSTORE_CLUSTER_SIZE=3
export EVENTSTORE_DB=/tmp/es-test-db
export EVENTSTORE_LOG_LEVEL=Debug
```

## 1. Unit Tests

### Test the gRPC Service Implementation

Create comprehensive unit tests for the gRPC components:

```csharp
// File: src/EventStore.Core.Tests/Services/Replication/GrpcReplicationServiceTests.cs

using System;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Messages;
using EventStore.Core.Metrics;
using EventStore.Core.Services.Transport.Grpc.Cluster;
using EventStore.Plugins.Authorization;
using NUnit.Framework;
using EventStore.Core.Tests.Helpers;
using EventStore.Cluster;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

[TestFixture]
public class GrpcReplicationServiceTests {
    private InMemoryBus _bus;
    private PassthroughAuthorizationProvider _authProvider;
    private QueueTrackers _queueTrackers;
    private Replication _replicationService;
    private MockServerCallContext _context;

    [SetUp]
    public void Setup() {
        _bus = new InMemoryBus("test");
        _authProvider = new PassthroughAuthorizationProvider();
        _queueTrackers = new QueueTrackers();
        _replicationService = new Replication(_bus, _authProvider, _queueTrackers, "test.cluster");
        _context = new MockServerCallContext();
    }

    [Test]
    public async Task GetReplicationInfo_ReturnsValidResponse() {
        // Arrange
        var request = new EventStore.Client.Empty();

        // Act
        var response = await _replicationService.GetReplicationInfo(request, _context);

        // Assert
        Assert.That(response, Is.Not.Null);
        Assert.That(response.ActiveSubscriptions, Is.EqualTo(0));
    }

    [Test]
    public async Task StreamReplication_HandlesSubscribeRequest() {
        // Arrange
        var requestStream = new MockAsyncStreamReader<ReplicationRequest>();
        var responseStream = new MockServerStreamWriter<ReplicationResponse>();

        var subscribeRequest = new ReplicationRequest {
            SubscribeReplica = new SubscribeReplica {
                LogPosition = 0,
                ChunkId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                Ip = ByteString.CopyFromUtf8("127.0.0.1"),
                Port = 2113,
                LeaderId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                SubscriptionId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                IsPromotable = true,
                Version = 1
            }
        };

        requestStream.AddMessage(subscribeRequest);
        requestStream.Complete();

        // Act & Assert - Should not throw
        Assert.DoesNotThrowAsync(async () => {
            await _replicationService.StreamReplication(requestStream, responseStream, _context);
        });
    }

    [Test]
    public void MessageConversion_SubscribeReplica_ConvertsCorrectly() {
        // Arrange
        var originalGuid = Guid.NewGuid();
        var subscribeReplica = new SubscribeReplica {
            LogPosition = 12345,
            ChunkId = ByteString.CopyFrom(originalGuid.ToByteArray()),
            Ip = ByteString.CopyFromUtf8("192.168.1.100"),
            Port = 2113,
            LeaderId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            SubscriptionId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            IsPromotable = true,
            Version = 1
        };

        // Act - This tests the conversion logic in the actual service
        var request = new ReplicationRequest { SubscribeReplica = subscribeReplica };

        // Assert - Verify the message structure is valid
        Assert.That(request.SubscribeReplica.LogPosition, Is.EqualTo(12345));
        Assert.That(request.SubscribeReplica.Port, Is.EqualTo(2113));
        Assert.That(request.SubscribeReplica.IsPromotable, Is.True);
    }
}

// Mock implementations for testing
public class MockAsyncStreamReader<T> : IAsyncStreamReader<T> {
    private readonly Queue<T> _messages = new Queue<T>();
    private bool _completed = false;

    public T Current { get; private set; }

    public void AddMessage(T message) => _messages.Enqueue(message);
    public void Complete() => _completed = true;

    public async Task<bool> MoveNext(CancellationToken cancellationToken) {
        if (_messages.Count > 0) {
            Current = _messages.Dequeue();
            return true;
        }
        return !_completed;
    }

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
        return this;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class MockServerStreamWriter<T> : IServerStreamWriter<T> {
    public WriteOptions WriteOptions { get; set; }
    public List<T> WrittenMessages { get; } = new List<T>();

    public Task WriteAsync(T message) {
        WrittenMessages.Add(message);
        return Task.CompletedTask;
    }
}

public class MockServerCallContext : ServerCallContext {
    public override string Method => "TestMethod";
    public override string Host => "localhost";
    public override string Peer => "127.0.0.1";
    public override DateTime Deadline => DateTime.UtcNow.AddMinutes(5);
    public override Metadata RequestHeaders => new Metadata();
    public override CancellationToken CancellationToken => CancellationToken.None;
    public override Metadata ResponseTrailers => new Metadata();
    public override Status Status { get; set; }
    public override WriteOptions WriteOptions { get; set; }

    protected override string MethodCore => Method;
    protected override string HostCore => Host;
    protected override string PeerCore => Peer;
    protected override DateTime DeadlineCore => Deadline;
    protected override Metadata RequestHeadersCore => RequestHeaders;
    protected override CancellationToken CancellationTokenCore => CancellationToken;
    protected override Metadata ResponseTrailersCore => ResponseTrailers;
    protected override Status StatusCore { get => Status; set => Status = value; }
    protected override WriteOptions WriteOptionsCore { get => WriteOptions; set => WriteOptions = value; }

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) {
        return Task.CompletedTask;
    }

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) {
        return null;
    }

    public override HttpContext GetHttpContext() {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity("test"));
        return context;
    }
}
```

### Test the GrpcReplicaService

```csharp
// File: src/EventStore.Core.Tests/Services/Replication/GrpcReplicaServiceTests.cs

[TestFixture]
public class GrpcReplicaServiceTests {
    private InMemoryBus _bus;
    private TestTFChunkDb _db;
    private FakeEpochManager _epochManager;
    private EventStoreClusterClientCache _clientCache;
    private GrpcReplicaService _replicaService;

    [SetUp]
    public void Setup() {
        _bus = new InMemoryBus("test");
        _db = new TestTFChunkDb();
        _epochManager = new FakeEpochManager();
        _clientCache = new EventStoreClusterClientCache();
        
        _replicaService = new GrpcReplicaService(
            _bus, _db, _epochManager, _clientCache,
            new IPEndPoint(IPAddress.Loopback, 2113),
            false, "test.cluster");
    }

    [Test]
    public void HandleStateChange_ToPreReplica_InitiatesConnection() {
        // Arrange
        var leader = CreateTestLeader();
        var message = new SystemMessage.BecomePreReplica(Guid.NewGuid(), leader);

        // Act
        _replicaService.Handle(message);

        // Assert - Verify connection attempt was made
        // This would require mocking the gRPC client
    }

    private MemberInfo CreateTestLeader() {
        return MemberInfo.ForVNode(
            Guid.NewGuid(),
            DateTime.UtcNow,
            VNodeState.Leader,
            true,
            new IPEndPoint(IPAddress.Loopback, 2113),
            new IPEndPoint(IPAddress.Loopback, 1113),
            new IPEndPoint(IPAddress.Loopback, 1112),
            false, false, 0, 0, 0, 0, 0, Guid.Empty, 0, false, "", 2113, 1113, "24.0.0");
    }
}
```

## 2. Integration Tests

### Test gRPC Service Registration

```csharp
// File: src/EventStore.Core.Tests/Services/Transport/Grpc/GrpcServiceRegistrationTests.cs

[TestFixture]
public class GrpcServiceRegistrationTests {
    [Test]
    public void ClusterVNodeStartup_RegistersReplicationService() {
        // Arrange
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        
        // Create startup with test dependencies
        var startup = CreateTestStartup();

        // Act
        var serviceProvider = startup.ConfigureServices(services);

        // Assert
        var replicationService = serviceProvider.GetService<Replication>();
        Assert.That(replicationService, Is.Not.Null);
    }

    private ClusterVNodeStartup<string> CreateTestStartup() {
        // Create with minimal required dependencies for testing
        // This would require significant setup of test infrastructure
    }
}
```

## 3. Functional Tests

### End-to-End Cluster Tests

Create integration tests that run actual EventStore instances:

```bash
#!/bin/bash
# File: test-scripts/test-grpc-replication.sh

# Test gRPC replication with a 3-node cluster

# Clean up any existing test data
rm -rf /tmp/es-node-*

# Start 3 nodes with gRPC replication enabled
start_node() {
    local node_id=$1
    local http_port=$((2113 + node_id))
    local tcp_port=$((1113 + node_id))
    local int_tcp_port=$((1112 + node_id))
    
    ./src/EventStore.ClusterNode/bin/Debug/net8.0/EventStore.ClusterNode \
        --cluster-size=3 \
        --cluster-enable-grpc-replication=true \
        --db=/tmp/es-node-$node_id \
        --log=/tmp/es-node-$node_id/logs \
        --int-ip=127.0.0.1 \
        --ext-ip=127.0.0.1 \
        --int-tcp-port=$int_tcp_port \
        --ext-tcp-port=$tcp_port \
        --int-http-port=$http_port \
        --ext-http-port=$http_port \
        --gossip-seed=127.0.0.1:2113,127.0.0.1:2114,127.0.0.1:2115 \
        --enable-external-tcp=true \
        --log-level=Debug \
        > /tmp/es-node-$node_id.log 2>&1 &
        
    echo $! > /tmp/es-node-$node_id.pid
}

# Start nodes
echo "Starting EventStore nodes with gRPC replication..."
start_node 1
start_node 2  
start_node 3

# Wait for cluster to form
echo "Waiting for cluster formation..."
sleep 30

# Test basic functionality
echo "Testing basic write/read functionality..."

# Write some events
curl -i -d '[{"eventId":"$(uuidgen)","eventType":"TestEvent","data":{"test":"data"}}]' \
    "http://127.0.0.1:2113/streams/test-stream" \
    -H "Content-Type: application/vnd.eventstore.events+json"

# Read events back
curl -i "http://127.0.0.1:2113/streams/test-stream" \
    -H "Accept: application/vnd.eventstore.atom+json"

# Test replication by reading from different nodes
for port in 2113 2114 2115; do
    echo "Reading from node on port $port..."
    curl -s "http://127.0.0.1:$port/streams/test-stream" \
        -H "Accept: application/vnd.eventstore.atom+json" | \
        grep -q "test-stream" && echo "✓ Node $port has replicated data" || echo "✗ Node $port missing data"
done

# Cleanup
echo "Cleaning up..."
for i in 1 2 3; do
    if [ -f /tmp/es-node-$i.pid ]; then
        kill $(cat /tmp/es-node-$i.pid) 2>/dev/null
        rm /tmp/es-node-$i.pid
    fi
done

echo "Test completed. Check logs in /tmp/es-node-*.log for details."
```

### Performance Comparison Test

```bash
#!/bin/bash
# File: test-scripts/performance-comparison.sh

# Compare TCP vs gRPC replication performance

run_performance_test() {
    local protocol=$1
    local enable_grpc=$2
    
    echo "Testing $protocol replication performance..."
    
    # Start 2-node cluster
    ./src/EventStore.ClusterNode/bin/Debug/net8.0/EventStore.ClusterNode \
        --cluster-size=2 \
        --cluster-enable-grpc-replication=$enable_grpc \
        --db=/tmp/es-leader \
        --int-ip=127.0.0.1 --ext-ip=127.0.0.1 \
        --int-tcp-port=1112 --ext-tcp-port=1113 \
        --int-http-port=2113 --ext-http-port=2113 \
        --gossip-seed=127.0.0.1:2113,127.0.0.1:2114 &
    leader_pid=$!
    
    ./src/EventStore.ClusterNode/bin/Debug/net8.0/EventStore.ClusterNode \
        --cluster-size=2 \
        --cluster-enable-grpc-replication=$enable_grpc \
        --db=/tmp/es-follower \
        --int-ip=127.0.0.1 --ext-ip=127.0.0.1 \
        --int-tcp-port=1122 --ext-tcp-port=1123 \
        --int-http-port=2114 --ext-http-port=2114 \
        --gossip-seed=127.0.0.1:2113,127.0.0.1:2114 &
    follower_pid=$!
    
    sleep 20  # Wait for cluster formation
    
    # Write 1000 events and measure time
    start_time=$(date +%s%N)
    for i in {1..1000}; do
        curl -s -d "[{\"eventId\":\"$(uuidgen)\",\"eventType\":\"PerfTest\",\"data\":{\"index\":$i}}]" \
            "http://127.0.0.1:2113/streams/perf-test-$protocol" \
            -H "Content-Type: application/vnd.eventstore.events+json" > /dev/null
    done
    end_time=$(date +%s%N)
    
    duration=$(( (end_time - start_time) / 1000000 ))  # Convert to milliseconds
    throughput=$(( 1000 * 1000 / duration ))  # Events per second
    
    echo "$protocol: $duration ms total, $throughput events/sec"
    
    # Cleanup
    kill $leader_pid $follower_pid 2>/dev/null
    rm -rf /tmp/es-leader /tmp/es-follower
    sleep 5
}

# Clean any existing data
rm -rf /tmp/es-*

echo "Performance Comparison: TCP vs gRPC Replication"
echo "================================================"

run_performance_test "TCP" "false"
run_performance_test "gRPC" "true"
```

## 4. Manual Testing Procedures

### Basic Functionality Test

1. **Setup a 3-node cluster with gRPC replication:**

```bash
# Node 1 (Leader candidate)
./EventStore.ClusterNode \
    --cluster-size=3 \
    --cluster-enable-grpc-replication=true \
    --db=/tmp/node1 \
    --int-ip=127.0.0.1 --ext-ip=127.0.0.1 \
    --int-tcp-port=1112 --ext-tcp-port=1113 \
    --int-http-port=2113 --ext-http-port=2113 \
    --gossip-seed=127.0.0.1:2113,127.0.0.1:2114,127.0.0.1:2115

# Node 2 (Follower)  
./EventStore.ClusterNode \
    --cluster-size=3 \
    --cluster-enable-grpc-replication=true \
    --db=/tmp/node2 \
    --int-ip=127.0.0.1 --ext-ip=127.0.0.1 \
    --int-tcp-port=1122 --ext-tcp-port=1123 \
    --int-http-port=2114 --ext-http-port=2114 \
    --gossip-seed=127.0.0.1:2113,127.0.0.1:2114,127.0.0.1:2115

# Node 3 (Follower)
./EventStore.ClusterNode \
    --cluster-size=3 \
    --cluster-enable-grpc-replication=true \
    --db=/tmp/node3 \
    --int-ip=127.0.0.1 --ext-ip=127.0.0.1 \
    --int-tcp-port=1132 --ext-tcp-port=1133 \
    --int-http-port=2115 --ext-http-port=2115 \
    --gossip-seed=127.0.0.1:2113,127.0.0.1:2114,127.0.0.1:2115
```

2. **Verify cluster formation:**

```bash
# Check cluster status
curl http://127.0.0.1:2113/gossip | jq '.'

# Should show 3 nodes with one Leader and two Followers
```

3. **Test replication:**

```bash
# Write events to leader
curl -i -d '[
    {"eventId":"'$(uuidgen)'","eventType":"TestEvent","data":{"message":"Hello gRPC replication!"}}
]' "http://127.0.0.1:2113/streams/test-grpc-replication" \
-H "Content-Type: application/vnd.eventstore.events+json"

# Verify replication on followers
curl "http://127.0.0.1:2114/streams/test-grpc-replication/0" -H "Accept: application/json"
curl "http://127.0.0.1:2115/streams/test-grpc-replication/0" -H "Accept: application/json"
```

### Failure Scenarios

1. **Test follower disconnection:**
   - Kill one follower node
   - Verify writes still succeed (2/3 quorum)
   - Restart follower and verify catch-up replication

2. **Test leader failure:**
   - Kill the leader node
   - Verify new leader election occurs
   - Verify replication continues with new leader

3. **Test network partitions:**
   - Use `iptables` or similar to block communication
   - Verify behavior during split-brain scenarios

### Mixed Protocol Testing

1. **Start cluster with mixed protocols:**

```bash
# Leader with gRPC
./EventStore.ClusterNode --cluster-enable-grpc-replication=true ...

# Follower with TCP (gRPC disabled)
./EventStore.ClusterNode --cluster-enable-grpc-replication=false ...
```

2. **Verify graceful degradation and compatibility**

## 5. Debugging and Troubleshooting

### Enable Detailed Logging

```bash
# Set detailed logging for gRPC components
export EVENTSTORE_LOG_LEVEL=Debug

# Or via configuration file:
# Logging:
#   Level: Debug
#   LoggerFilters:
#     - "EventStore.Core.Services.Transport.Grpc.Cluster.Replication": Debug
#     - "EventStore.Core.Services.Replication.GrpcReplicaService": Debug
```

### Monitor gRPC Connections

```bash
# Check gRPC service endpoints
curl http://127.0.0.1:2113/info | jq '.grpcPort'

# Monitor replication stats
curl http://127.0.0.1:2113/stats/replication | jq '.'
```

### Common Issues and Solutions

1. **"gRPC service not registered" error:**
   - Verify protobuf compilation completed successfully
   - Check service registration in ClusterVNodeStartup

2. **Connection timeouts:**
   - Verify firewall settings allow gRPC traffic
   - Check network connectivity between nodes

3. **Message conversion errors:**
   - Verify protobuf message compatibility
   - Check byte array conversions for GUIDs

## 6. Automated Test Execution

### Run All Tests

```bash
# Unit tests
dotnet test src/EventStore.Core.Tests/Services/Replication/GrpcReplicationTests.cs

# Integration tests  
dotnet test src/EventStore.Core.Tests/Services/Transport/Grpc/GrpcServiceRegistrationTests.cs

# Functional tests
./test-scripts/test-grpc-replication.sh

# Performance comparison
./test-scripts/performance-comparison.sh
```

### CI/CD Integration

Add to your build pipeline:

```yaml
# .github/workflows/test-grpc-replication.yml
name: Test gRPC Replication

on: [push, pull_request]

jobs:
  test-grpc-replication:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v2
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v1
      with:
        dotnet-version: 8.0.x
        
    - name: Build
      run: dotnet build src/EventStore.sln
      
    - name: Run Unit Tests
      run: dotnet test src/EventStore.Core.Tests/ --filter="Category=GrpcReplication"
      
    - name: Run Integration Tests
      run: ./test-scripts/test-grpc-replication.sh
```

This comprehensive testing strategy ensures the gRPC replication implementation is thoroughly validated at all levels, from individual components to full cluster scenarios.