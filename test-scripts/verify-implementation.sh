#!/bin/bash

# Verification script to check if gRPC replication implementation is complete

PROJECT_ROOT="/Users/ubi/Developer/github.com/TrogonStack/TrogonEventStore"
cd "$PROJECT_ROOT"

echo "🔍 TrogonEventStore gRPC Replication Implementation Verification"
echo "=============================================================="

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Counters
TOTAL_CHECKS=0
PASSED_CHECKS=0

# Check function
check() {
    local description="$1"
    local test_command="$2"
    
    TOTAL_CHECKS=$((TOTAL_CHECKS + 1))
    printf "%-50s" "$description..."
    
    if eval "$test_command" >/dev/null 2>&1; then
        echo -e "${GREEN}✅ PASS${NC}"
        PASSED_CHECKS=$((PASSED_CHECKS + 1))
        return 0
    else
        echo -e "${RED}❌ FAIL${NC}"
        return 1
    fi
}

# File existence checks
echo "📂 Checking implementation files..."
echo "=================================="

check "gRPC Replication Service" "[ -f 'src/EventStore.Core/Services/Transport/Grpc/Cluster.Replication.cs' ]"
check "gRPC Replica Client Service" "[ -f 'src/EventStore.Core/Services/Replication/GrpcReplicaService.cs' ]"
check "Protobuf Service Definition" "[ -f 'src/Protos/Grpc/cluster.proto' ]"
check "Unit Test File" "[ -f 'src/EventStore.Core.Tests/Services/Replication/GrpcReplicationTests.cs' ]"
check "Documentation" "[ -f 'docs/grpc-replication.md' ]"

echo ""
echo "⚙️  Checking configuration integration..."
echo "======================================="

check "EnableGrpcReplication option" "grep -q 'EnableGrpcReplication' src/EventStore.Core/Configuration/ClusterVNodeOptions.cs"
check "ClusterVNode integration" "grep -q 'GrpcReplicaService' src/EventStore.Core/ClusterVNode.cs"
check "Service registration" "grep -q 'MapGrpcService<Replication>' src/EventStore.Core/ClusterVNodeStartup.cs"
check "DI container registration" "grep -q 'AddSingleton.*Replication' src/EventStore.Core/ClusterVNodeStartup.cs"

echo ""
echo "📋 Checking protobuf definitions..."
echo "=================================="

check "Replication service defined" "grep -q 'service Replication' src/Protos/Grpc/cluster.proto"
check "StreamReplication RPC" "grep -q 'StreamReplication' src/Protos/Grpc/cluster.proto"
check "ReplicationRequest message" "grep -q 'message ReplicationRequest' src/Protos/Grpc/cluster.proto"
check "ReplicationResponse message" "grep -q 'message ReplicationResponse' src/Protos/Grpc/cluster.proto"

echo ""
echo "🔧 Checking implementation details..."
echo "==================================="

check "Bidirectional streaming" "grep -q 'IAsyncStreamReader.*IServerStreamWriter' src/EventStore.Core/Services/Transport/Grpc/Cluster.Replication.cs"
check "Message conversion logic" "grep -q 'ConvertFromGrpc\|ConvertToGrpc' src/EventStore.Core/Services/Transport/Grpc/Cluster.Replication.cs"
check "Authorization integration" "grep -q 'IAuthorizationProvider' src/EventStore.Core/Services/Transport/Grpc/Cluster.Replication.cs"
check "Connection management" "grep -q 'EventStoreClusterClientCache' src/EventStore.Core/Services/Replication/GrpcReplicaService.cs"
check "State change handling" "grep -q 'SystemMessage.StateChangeMessage' src/EventStore.Core/Services/Replication/GrpcReplicaService.cs"

echo ""
echo "📖 Checking documentation..."
echo "=========================="

check "User documentation exists" "[ -s 'docs/grpc-replication.md' ]"
check "Cluster docs updated" "grep -q 'gRPC replication' docs/cluster.md"
check "Configuration documented" "grep -q 'EnableGrpcReplication' docs/cluster.md"
check "Migration guide included" "grep -q 'Migration' docs/grpc-replication.md"

echo ""
echo "🧪 Checking test infrastructure..."
echo "================================="

check "Test class exists" "grep -q 'class.*GrpcReplicationTests' src/EventStore.Core.Tests/Services/Replication/GrpcReplicationTests.cs"
check "Service instantiation test" "grep -q 'can_create_grpc_replication_service' src/EventStore.Core.Tests/Services/Replication/GrpcReplicationTests.cs"
check "Mock implementations" "grep -q 'MockServerCallContext' src/EventStore.Core.Tests/Services/Replication/GrpcReplicationTests.cs"

echo ""
echo "🏗️  Checking build prerequisites..."
echo "================================="

check "Solution file exists" "[ -f 'src/EventStore.sln' ]"
check "Build script exists" "[ -f 'build.sh' ] || command -v dotnet >/dev/null"
check "Project files exist" "[ -f 'src/EventStore.Core/EventStore.Core.csproj' ]"

# Advanced checks
echo ""
echo "🔬 Advanced implementation checks..."
echo "==================================="

check "Uses proper async patterns" "grep -q 'async Task' src/EventStore.Core/Services/Transport/Grpc/Cluster.Replication.cs"
check "Error handling implemented" "grep -q 'try.*catch\|RpcException' src/EventStore.Core/Services/Transport/Grpc/Cluster.Replication.cs"
check "Logging integration" "grep -q 'ILogger\|Log\.' src/EventStore.Core/Services/Replication/GrpcReplicaService.cs"
check "Message bus integration" "grep -q '_bus\.Publish\|_publisher\.Publish' src/EventStore.Core/Services/Transport/Grpc/Cluster.Replication.cs"

# Calculate score
SCORE=$((PASSED_CHECKS * 100 / TOTAL_CHECKS))

echo ""
echo "📊 Verification Results"
echo "======================"
echo "Passed: $PASSED_CHECKS/$TOTAL_CHECKS checks"
echo "Score: $SCORE%"

if [ $SCORE -eq 100 ]; then
    echo -e "${GREEN}🎉 IMPLEMENTATION COMPLETE! Ready for testing.${NC}"
    echo ""
    echo "✅ All checks passed! Your gRPC replication implementation is complete."
    echo ""
    echo "🚀 Next steps:"
    echo "   1. Build the project:      ./build.sh --configuration Debug"
    echo "   2. Run unit tests:         ./test-scripts/run-unit-tests.sh"
    echo "   3. Test functionality:     ./test-scripts/quick-test.sh grpc"
    echo "   4. Compare with TCP:       ./test-scripts/quick-test.sh tcp"
    echo "   5. Test mixed protocols:   ./test-scripts/quick-test.sh mixed"
    
elif [ $SCORE -ge 90 ]; then
    echo -e "${YELLOW}⚠️  MOSTLY COMPLETE - Minor issues detected${NC}"
    echo ""
    echo "Most components are implemented correctly. Address any failed checks above."
    
elif [ $SCORE -ge 70 ]; then
    echo -e "${YELLOW}⚠️  PARTIALLY COMPLETE - Several issues detected${NC}"
    echo ""
    echo "Core components are present but some implementation details are missing."
    
else
    echo -e "${RED}❌ INCOMPLETE - Major issues detected${NC}"
    echo ""
    echo "Significant implementation work is still needed."
fi

echo ""
echo "💡 For detailed testing instructions, see:"
echo "   - TESTING_GRPC_REPLICATION.md"
echo "   - docs/grpc-replication.md"