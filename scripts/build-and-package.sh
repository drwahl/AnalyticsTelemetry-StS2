#!/usr/bin/env bash
set -euo pipefail

# One-shot: Release build + zip under dist/ for manual Nexus / Discord upload.

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
"$ROOT/scripts/build-release.sh"
"$ROOT/scripts/package-mod-zip.sh"
