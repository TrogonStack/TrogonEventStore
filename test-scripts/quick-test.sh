#!/bin/bash

# Quick test script for gRPC replication
# This provides a simple way to test the basic functionality

set -e

echo "🚀 TrogonEventStore gRPC Replication Quick Test"
echo "=============================================="

# Configuration
PROJECT_ROOT="/Users/ubi/Developer/github.com/TrogonStack/TrogonEventStore"
TEST_DIR="/tmp/trogon-test"
BINARY_PATH="$PROJECT_ROOT/src/EventStore.ClusterNode/bin/Debug/net8.0/EventStore.ClusterNode"

# Clean up function
cleanup() {
    echo "🧹 Cleaning up..."
    for i in 1 2 3; do
        if [ -f "$TEST_DIR/node$i.pid" ]; then
            kill $(cat "$TEST_DIR/node$i.pid") 2>/dev/null || true
            rm -f "$TEST_DIR/node$i.pid"
        fi
    done
    rm -rf "$TEST_DIR"
    echo "✅ Cleanup completed"
}

# Set up trap for cleanup
trap cleanup EXIT

# Create test directory
mkdir -p "$TEST_DIR"

# Check if binary exists
if [ ! -f "$BINARY_PATH" ]; then
    echo "❌ EventStore binary not found at: $BINARY_PATH"
    echo "   Please build the project first:"
    echo "   cd $PROJECT_ROOT && ./build.sh --configuration Debug"
    exit 1
fi

# Function to start a node
start_node() {
    local node_id=$1
    local enable_grpc=$2
    local http_port=$((2112 + node_id))
    local tcp_port=$((1112 + node_id))
    local int_tcp_port=$((1122 + node_id))
    
    echo "🌟 Starting Node $node_id (gRPC: $enable_grpc)..."
    
    "$BINARY_PATH" \
        --cluster-size=3 \
        --cluster-enable-grpc-replication="$enable_grpc" \
        --db="$TEST_DIR/node$node_id/db" \
        --log="$TEST_DIR/node$node_id/logs" \
        --int-ip=127.0.0.1 \
        --ext-ip=127.0.0.1 \
        --int-tcp-port=$int_tcp_port \
        --ext-tcp-port=$tcp_port \
        --int-http-port=$http_port \
        --ext-http-port=$http_port \
        --gossip-seed=127.0.0.1:2113,127.0.0.1:2114,127.0.0.1:2115 \
        --enable-external-tcp=true \
        --log-level=Info \
        --insecure \
        > "$TEST_DIR/node$node_id.log" 2>&1 &
        
    echo $! > "$TEST_DIR/node$node_id.pid"
    echo "✅ Node $node_id started (PID: $(cat "$TEST_DIR/node$node_id.pid"))"
}

# Function to wait for node to be ready
wait_for_node() {
    local port=$1
    local node_name=$2
    local max_attempts=30
    local attempt=1
    
    echo "⏳ Waiting for $node_name to be ready..."
    
    while [ $attempt -le $max_attempts ]; do
        if curl -s "http://127.0.0.1:$port/info" > /dev/null 2>&1; then
            echo "✅ $node_name is ready!"
            return 0
        fi
        echo "   Attempt $attempt/$max_attempts..."
        sleep 2
        attempt=$((attempt + 1))
    done
    
    echo "❌ $node_name failed to start after $max_attempts attempts"
    return 1
}

# Function to check cluster status
check_cluster_status() {
    echo "📊 Checking cluster status..."
    
    local gossip_response=$(curl -s "http://127.0.0.1:2113/gossip" 2>/dev/null || echo "ERROR")
    
    if [ "$gossip_response" = "ERROR" ]; then
        echo "❌ Failed to get gossip response"
        return 1
    fi
    
    echo "✅ Gossip response received"
    echo "$gossip_response" | jq '.' 2>/dev/null || echo "$gossip_response"
    
    # Count nodes in different states
    local leader_count=$(echo "$gossip_response" | jq -r '.members[] | select(.state == "Leader") | .state' 2>/dev/null | wc -l || echo "0")
    local follower_count=$(echo "$gossip_response" | jq -r '.members[] | select(.state == "Follower") | .state' 2>/dev/null | wc -l || echo "0")
    
    echo "📈 Cluster composition: $leader_count Leader(s), $follower_count Follower(s)"
    
    if [ "$leader_count" -eq 1 ] && [ "$follower_count" -eq 2 ]; then
        echo "✅ Cluster is healthy!"
        return 0
    else
        echo "⚠️  Cluster may not be fully formed yet"
        return 1
    fi
}

# Function to test replication
test_replication() {
    echo "🔄 Testing replication..."
    
    local stream_name="test-grpc-replication-$(date +%s)"
    local event_data='[{"eventId":"'$(uuidgen)'","eventType":"TestEvent","data":{"message":"Hello gRPC replication!","timestamp":"'$(date -u +%Y-%m-%dT%H:%M:%SZ)'"}}]'
    
    echo "📝 Writing event to stream: $stream_name"
    
    local write_response=$(curl -s -w "%{http_code}" -o /tmp/write_response.json \
        -X POST \
        -H "Content-Type: application/vnd.eventstore.events+json" \
        -H "ES-ExpectedVersion: -2" \
        -d "$event_data" \
        "http://127.0.0.1:2113/streams/$stream_name")
    
    if [ "$write_response" = "201" ]; then
        echo "✅ Event written successfully"
    else
        echo "❌ Failed to write event (HTTP $write_response)"
        cat /tmp/write_response.json 2>/dev/null || true
        return 1
    fi
    
    # Wait a moment for replication
    sleep 3
    
    # Test reading from all nodes
    echo "📖 Testing reads from all nodes..."
    
    for port in 2113 2114 2115; do
        local node_num=$((port - 2112))
        echo "   Reading from Node $node_num (port $port)..."
        
        local read_response=$(curl -s -w "%{http_code}" -o /tmp/read_response_$port.json \
            -H "Accept: application/vnd.eventstore.atom+json" \
            "http://127.0.0.1:$port/streams/$stream_name")
        
        if [ "$read_response" = "200" ]; then
            local event_count=$(cat /tmp/read_response_$port.json | jq '.entries | length' 2>/dev/null || echo "0")
            if [ "$event_count" -gt 0 ]; then
                echo "   ✅ Node $node_num: Event replicated successfully ($event_count events)"
            else
                echo "   ⚠️  Node $node_num: No events found"
            fi
        else
            echo "   ❌ Node $node_num: Failed to read (HTTP $read_response)"
        fi
    done
    
    echo "✅ Replication test completed"
}

# Function to show logs
show_logs() {
    echo "📋 Recent log entries:"
    echo "===================="
    
    for i in 1 2 3; do
        if [ -f "$TEST_DIR/node$i.log" ]; then
            echo "--- Node $i logs (last 10 lines) ---"
            tail -10 "$TEST_DIR/node$i.log" | sed 's/^/   /'
            echo ""
        fi
    done
}

# Main test execution
main() {
    echo "🔧 Test configuration:"
    echo "   Project root: $PROJECT_ROOT"
    echo "   Test directory: $TEST_DIR"
    echo "   Binary path: $BINARY_PATH"
    echo ""
    
    # Parse command line arguments
    local protocol="grpc"
    if [ "$1" = "tcp" ]; then
        protocol="tcp"
    elif [ "$1" = "mixed" ]; then
        protocol="mixed"
    fi
    
    echo "🎯 Testing protocol: $protocol"
    echo ""
    
    # Start nodes based on protocol choice
    case "$protocol" in
        "grpc")
            start_node 1 true
            start_node 2 true  
            start_node 3 true
            ;;
        "tcp")
            start_node 1 false
            start_node 2 false
            start_node 3 false
            ;;
        "mixed")
            start_node 1 true   # Leader with gRPC
            start_node 2 false  # Follower with TCP
            start_node 3 true   # Follower with gRPC
            ;;
    esac
    
    # Wait for nodes to start
    echo ""
    wait_for_node 2113 "Node 1" || exit 1
    wait_for_node 2114 "Node 2" || exit 1
    wait_for_node 2115 "Node 3" || exit 1
    
    # Wait for cluster formation
    echo ""
    echo "⏳ Waiting for cluster formation..."
    sleep 15
    
    # Check cluster status
    echo ""
    check_cluster_status
    
    # Test replication
    echo ""
    test_replication
    
    # Show some logs for debugging
    echo ""
    show_logs
    
    echo ""
    echo "🎉 Test completed successfully!"
    echo ""
    echo "💡 Tips:"
    echo "   - Check full logs at: $TEST_DIR/node*.log"
    echo "   - Access web UI at: http://127.0.0.1:2113"
    echo "   - View gossip at: http://127.0.0.1:2113/gossip"
    echo "   - Monitor stats at: http://127.0.0.1:2113/stats"
}

# Check for help
if [ "$1" = "--help" ] || [ "$1" = "-h" ]; then
    echo "Usage: $0 [protocol]"
    echo ""
    echo "Protocols:"
    echo "  grpc   - Test with gRPC replication (default)"
    echo "  tcp    - Test with TCP replication"
    echo "  mixed  - Test with mixed protocols"
    echo ""
    echo "Examples:"
    echo "  $0        # Test gRPC replication"
    echo "  $0 grpc   # Test gRPC replication"
    echo "  $0 tcp    # Test TCP replication"
    echo "  $0 mixed  # Test mixed protocols"
    exit 0
fi

# Run the main test
main "$@"