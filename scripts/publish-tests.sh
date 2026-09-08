#!/usr/bin/env sh

set -eu

source_directory=$1
output_directory=$2
staging_directory=$(mktemp -d)
trap 'rm -rf "$staging_directory"' EXIT
test_projects="$staging_directory/test-projects"
node_publish_directory="$staging_directory/node"

# Test projects do not inherit the web SDK's published static-asset manifest.
dotnet publish \
	--runtime="${RUNTIME}" \
	--no-self-contained \
	--configuration Release \
	--output "$node_publish_directory" \
	"$source_directory/EventStore.ClusterNode"

find "$source_directory" -maxdepth 2 -type f -name "*.Tests.csproj" -print > "$test_projects"

while IFS= read -r test_project; do
	test_output_directory="$output_directory/$(basename "$test_project" .csproj)"
	dotnet publish \
		--runtime="${RUNTIME}" \
		--no-self-contained \
		--configuration Release \
		--output "$test_output_directory" \
		"$test_project"
	cp "$node_publish_directory/EventStore.ClusterNode.staticwebassets.endpoints.json" "$test_output_directory/"
	cp -R "$node_publish_directory/wwwroot" "$test_output_directory/"
done < "$test_projects"
