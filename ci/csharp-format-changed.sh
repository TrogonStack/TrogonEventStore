#!/usr/bin/env bash
set -euo pipefail

solution="${1:?solution path is required}"
base_ref="${2:?base ref is required}"
head_ref="${3:-HEAD}"
solution_dir="$(dirname "$solution")"
solution_file="$(basename "$solution")"

if [[ "$base_ref" =~ ^0+$ ]]; then
	base_ref="$(git rev-list --max-parents=0 "$head_ref")"
fi

comparison_base="$base_ref"
if merge_base="$(git merge-base "$base_ref" "$head_ref" 2>/dev/null)"; then
	comparison_base="$merge_base"
fi

mapfile -t changed_files < <(git diff --name-only --diff-filter=ACMR "$comparison_base" "$head_ref" -- "*.cs")

include_files=()
for file in "${changed_files[@]}"; do
	if [[ "$solution_dir" == "." ]]; then
		include_files+=("$file")
	elif [[ "$file" == "$solution_dir"/* ]]; then
		include_files+=("${file#"$solution_dir"/}")
	else
		echo "Skipping C# file outside $solution_file: $file"
	fi
done

if ((${#include_files[@]} == 0)); then
	echo "No changed C# files to format under $solution_dir."
	exit 0
fi

printf "Checking C# formatting for:\n"
printf "  %s\n" "${include_files[@]}"

(
	cd "$solution_dir"
	dotnet format whitespace "$solution_file" --verify-no-changes --no-restore --include "${include_files[@]}"
	dotnet format style "$solution_file" --verify-no-changes --no-restore --diagnostics IDE0005 IDE0059 --severity info --include "${include_files[@]}"
)
