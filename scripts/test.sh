#!/usr/bin/env bash

set -o pipefail
set -o xtrace

update-ca-certificates

core_projects=(
    EventStore.Core.Tests
    EventStore.Core.XUnit.Tests
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
        core)
            requested_projects=("${core_projects[@]}")
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
    local declared_projects actual_projects duplicate_projects missing_assignments stale_assignments

    declared_projects="$(
        printf '%s\n' \
            "${core_projects[@]}" \
            "${projections_projects[@]}" \
            "${misc_projects[@]}" \
            | sort
    )"

    duplicate_projects="$(
        printf '%s\n' \
            "${core_projects[@]}" \
            "${projections_projects[@]}" \
            "${misc_projects[@]}" \
            | sort \
            | uniq -d
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

    if [ -n "$duplicate_projects" ]; then
        echo "Shard definitions contain duplicate test projects:" >&2
        printf '%s\n' "$duplicate_projects" >&2
        exit 1
    fi

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

    dotnet test --blame --blame-hang-timeout 5min \
        --settings /build/ci/ci.runsettings \
        --logger:html --logger:trx \
        --logger:"console;verbosity=minimal" \
        --results-directory "/build/test-results/$proj" \
        "$testdir/$proj.dll"
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

if [ -d "/build/test-results" ]; then
    find /build/test-results -name "*.html" -exec cat {} \; > /build/test-results/test-results.html 2>/dev/null || true
fi

exit ${exit_code:-0}
