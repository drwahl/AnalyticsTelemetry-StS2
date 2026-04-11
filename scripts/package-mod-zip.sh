#!/usr/bin/env bash
set -euo pipefail

# Zip the installable mod folder after `dotnet build -c Release`.
# Expects this repo next to the game: ../mods/AnalyticsTelemetry/

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MOD_NAME="AnalyticsTelemetry"
GAME_MODS="${ROOT}/../mods/${MOD_NAME}"

if [[ ! -f "${GAME_MODS}/${MOD_NAME}.dll" ]]; then
  echo "error: ${GAME_MODS}/${MOD_NAME}.dll not found." >&2
  echo "  Build from this repo first: dotnet build -c Release" >&2
  echo "  (Output is copied to ../mods/${MOD_NAME}/ by the project.)" >&2
  exit 1
fi

VER_LINE="$(grep -m1 '<Version>' "${ROOT}/AnalyticsTelemetry.csproj" | sed -E 's/.*<Version>([^<]+)<\/Version>.*/\1/')"
OUT_DIR="${ROOT}/dist"
OUT_ZIP="${OUT_DIR}/${MOD_NAME}-${VER_LINE}.zip"
mkdir -p "${OUT_DIR}"

( cd "${GAME_MODS}" && zip -r -q "${OUT_ZIP}" . )
echo "Wrote ${OUT_ZIP}"
