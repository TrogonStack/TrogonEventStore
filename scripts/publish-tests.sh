#!/usr/bin/env sh

set -eu

source_directory=$1
output_directory=$2

find "$source_directory" -maxdepth 1 -type d -name "*.Tests" -print | while IFS= read -r test_project; do
	dotnet publish \
		--runtime="${RUNTIME}" \
		--no-self-contained \
		--configuration Release \
		--output "$output_directory/$(basename "$test_project")" \
		"$test_project"
done
