#!/usr/bin/env bash
# Pre-push guard: stellt sicher, dass der in Plugin.cs einkompilierte
# Watermark-String exakt der in version.json deklarierten Build-Nummer entspricht.
# Bei Mismatch: Abbruch (exit 1) -> verhindert "silent failure" (falsche Watermark).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PLUGIN="$ROOT/Plugin.cs"
VERSION="$ROOT/version.json"

if [[ ! -f "$PLUGIN" || ! -f "$VERSION" ]]; then
  echo "[check-build-number] Plugin.cs oder version.json nicht gefunden — Skip (exit 0)." >&2
  exit 0
fi

# Build-Nummer aus Plugin.cs: customBuildNumber = "custom-build-XXX"
PLUGIN_BUILD="$(grep -oE 'customBuildNumber[[:space:]]*=[[:space:]]*"[^"]+"' "$PLUGIN" | grep -oE 'custom-build-[0-9]+')"
# Build-Nummer aus version.json: "build": "custom-build-XXX"
JSON_BUILD="$(grep -oE '"build"[[:space:]]*:[[:space:]]*"custom-build-[0-9]+"' "$VERSION" | grep -oE 'custom-build-[0-9]+')"

if [[ -z "$PLUGIN_BUILD" ]]; then
  echo "[check-build-number] FEHLER: customBuildNumber nicht in Plugin.cs gefunden." >&2
  exit 1
fi
if [[ -z "$JSON_BUILD" ]]; then
  echo "[check-build-number] FEHLER: build nicht in version.json gefunden." >&2
  exit 1
fi

if [[ "$PLUGIN_BUILD" != "$JSON_BUILD" ]]; then
  echo "[check-build-number] MISMATCH: Plugin.cs='$PLUGIN_BUILD' != version.json='$JSON_BUILD'" >&2
  echo "[check-build-number] Fix: Plugin.cs customBuildNumber auf '$JSON_BUILD' setzen, neu bauen, DLL repacken." >&2
  exit 1
fi

echo "[check-build-number] OK: '$PLUGIN_BUILD' stimmt in Plugin.cs und version.json ueberein."
exit 0
