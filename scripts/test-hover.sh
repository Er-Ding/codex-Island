#!/bin/bash
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$project_dir"
module_cache="$project_dir/.build/hover-check-module-cache"
mkdir -p "$module_cache"
export CLANG_MODULE_CACHE_PATH="$module_cache"
swiftc -module-cache-path "$module_cache" \
  Sources/CodexIsland/HoverTracking.swift Tests/HoverChecks.swift \
  -o .build/hover-checks
.build/hover-checks
