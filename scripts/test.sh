#!/usr/bin/env bash

set -o pipefail
set -o xtrace

update-ca-certificates

core_clientapi_projects=(
    EventStore.Core.Tests
)

core_rest_projects=(
    EventStore.Core.Tests
    EventStore.Core.XUnit.Tests
)

core_http_projects=(
    EventStore.Core.Tests
)

projections_projects=(
    EventStore.Projections.Core.Javascript.Tests
    EventStore.Projections.Core.Tests
    EventStore.Projections.Core.XUnit.Tests
)

misc_projects=(
    EventStore.LogV3.Tests
    EventStore.BufferManagement.Tests
    EventStore.Common.Tests
    EventStore.SourceGenerators.Tests
    EventStore.SystemRuntime.Tests
)

declare -a requested_projects

load_requested_projects() {
    case "${TEST_GROUP:-all}" in
        all)
            requested_projects=()
            ;;
        core-clientapi)
            requested_projects=("${core_clientapi_projects[@]}")
            ;;
        core-rest)
            requested_projects=("${core_rest_projects[@]}")
            ;;
        core-http)
            requested_projects=("${core_http_projects[@]}")
            ;;
        projections)
            requested_projects=("${projections_projects[@]}")
            ;;
        misc)
            requested_projects=("${misc_projects[@]}")
            ;;
        *)
            echo "Unknown TEST_GROUP '${TEST_GROUP}'." >&2
            exit 1
            ;;
    esac
}

validate_shard_coverage() {
    local declared_projects actual_projects missing_assignments stale_assignments

    declared_projects="$(
        printf '%s\n' \
            "${core_clientapi_projects[@]}" \
            "${core_rest_projects[@]}" \
            "${core_http_projects[@]}" \
            "${projections_projects[@]}" \
            "${misc_projects[@]}" \
            | sort \
            | uniq
    )"

    actual_projects="$(
        find /build/published-tests -maxdepth 1 -type d -name "*.Tests" -exec basename {} \; | sort
    )"

    missing_assignments="$(
        comm -23 \
            <(printf '%s\n' "$actual_projects") \
            <(printf '%s\n' "$declared_projects")
    )"

    stale_assignments="$(
        comm -13 \
            <(printf '%s\n' "$actual_projects") \
            <(printf '%s\n' "$declared_projects")
    )"

    if [ -n "$missing_assignments" ]; then
        echo "Published test projects are missing shard assignments:" >&2
        printf '%s\n' "$missing_assignments" >&2
        exit 1
    fi

    if [ -n "$stale_assignments" ]; then
        echo "Shard definitions reference test projects that are no longer published:" >&2
        printf '%s\n' "$stale_assignments" >&2
        exit 1
    fi
}

run_project() {
    local proj="$1"
    local testdir="$2"
    local -a dotnet_args
    local test_filter
    local timeout_window

    test_filter="$(project_filter "$proj")"
    timeout_window="$(project_timeout "$proj")"

    dotnet_args=(
        test
        --settings /build/ci/ci.runsettings
        --logger:"console;verbosity=minimal"
    )

    if [ -n "$test_filter" ]; then
        dotnet_args+=(--filter "$test_filter")
    fi

    dotnet_args+=("$testdir/$proj.dll")

    timeout --signal=TERM --kill-after=30s "$timeout_window" dotnet "${dotnet_args[@]}"

    local exit_code=$?

    if [ "$exit_code" -eq 124 ]; then
        echo "Test project '$proj' exceeded its ${timeout_window} limit." >&2
    fi

    return "$exit_code"
}

project_filter() {
    local proj="$1"

    case "${TEST_GROUP:-all}:$proj" in
        core-clientapi:EventStore.Core.Tests)
            printf '%s\n' "FullyQualifiedName~EventStore.Core.Tests.ClientAPI"
            ;;
        core-http:EventStore.Core.Tests)
            printf '%s\n' "(FullyQualifiedName~EventStore.Core.Tests.Http|FullyQualifiedName~EventStore.Core.Tests.Services.Transport.Http)&FullyQualifiedName!~EventStore.Core.Tests.ClientAPI"
            ;;
        core-rest:EventStore.Core.Tests)
            printf '%s\n' "FullyQualifiedName!~EventStore.Core.Tests.ClientAPI&FullyQualifiedName!~EventStore.Core.Tests.Http&FullyQualifiedName!~EventStore.Core.Tests.Services.Transport.Http"
            ;;
    esac
}

project_timeout() {
    local proj="$1"

    case "${TEST_GROUP:-all}:$proj" in
        core-clientapi:EventStore.Core.Tests)
            printf '%s\n' "15m"
            ;;
        core-rest:EventStore.Core.Tests)
            printf '%s\n' "25m"
            ;;
        core-http:EventStore.Core.Tests)
            printf '%s\n' "15m"
            ;;
        *:EventStore.Core.Tests)
            printf '%s\n' "30m"
            ;;
        *:EventStore.Core.XUnit.Tests|*:EventStore.Projections.Core.Tests)
            printf '%s\n' "15m"
            ;;
        *)
            printf '%s\n' "10m"
            ;;
    esac
}

run_project_or_stop() {
    local proj="$1"
    local testdir="$2"

    run_project "$proj" "$testdir"
    exit_code=$?
    return "$exit_code"
}

validate_shard_coverage
load_requested_projects

if [ ${#requested_projects[@]} -gt 0 ]; then
    for proj in "${requested_projects[@]}"; do
        testdir="/build/published-tests/$proj"

        if [ ! -f "$testdir/$proj.dll" ]; then
            echo "Requested test project '$proj' was not found in /build/published-tests." >&2
            exit_code=1
            break
        fi

        if ! run_project_or_stop "$proj" "$testdir"; then
            break
        fi
    done
else
    while IFS= read -r -d '' testdir; do
        proj=$(basename "$testdir")

        if ! run_project_or_stop "$proj" "$testdir"; then
            break
        fi
    done < <(find /build/published-tests -maxdepth 1 -type d -name "*.Tests" -print0)
fi

exit ${exit_code:-0}
