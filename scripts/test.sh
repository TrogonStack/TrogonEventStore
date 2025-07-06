#!/usr/bin/env bash

set -o pipefail
set -o xtrace

update-ca-certificates

find /build/published-tests -maxdepth 1 -type d -name "*.Tests" -print0 | \
while IFS= read -r -d '' testdir; do
    proj=$(basename "$testdir")
    dotnet test --blame --blame-hang-timeout 5min \
        --settings /build/ci/ci.runsettings \
        --logger:"GitHubActions;report-warnings=false" \
        --logger:html --logger:trx \
        --logger:"console;verbosity=normal" \
        --results-directory "/build/test-results/$proj" \
        "$testdir/$proj.dll" || exit_code=$?
done

if [ -d "/build/test-results" ]; then
    find /build/test-results -name "*.html" -exec cat {} \; > /build/test-results/test-results.html 2>/dev/null || true
fi

exit ${exit_code:-0}
