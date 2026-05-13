#!/usr/bin/env sh

set -eu

source_directory=$1
output_directory=$2
test_projects=$(mktemp)
trap 'rm -f "$test_projects"' EXIT

find "$source_directory" -maxdepth 1 -type d -name "*.Tests" -print > "$test_projects"

while IFS= read -r test_project; do
	dotnet publish \
		--runtime="${RUNTIME}" \
		--no-self-contained \
		--configuration Release \
		--output "$output_directory/$(basename "$test_project")" \
		"$test_project"
done < "$test_projects"
