#!/usr/bin/env bash
set -euo pipefail

# Release build; copies dll + manifest to ../mods/AnalyticsTelemetry/ (see AnalyticsTelemetry.csproj).

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
dotnet build -c Release AnalyticsTelemetry.csproj
