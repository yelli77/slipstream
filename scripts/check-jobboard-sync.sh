#!/usr/bin/env bash
# Gate: Headless-Integrationstest fuer die Job-Board-Sync-Kette (custom-build-365).
#
# Baut und laeuft den tests/JobBoardSmokeTest im dotnet-sdk-Container (net6, REINE
# common/dedicated-Referenzen — kein BepInEx/Unity noetig). Der Test startet die echte
# Dedicated-Server-Schicht (MessageHandler + JobBoardServer + JobBoardStore) in-process,
# verbindet zwei echte Riptide-Clients und prueft die komplette
# Upload -> Pool-Merge -> Broadcast -> jobTaken-Kette inkl. Burst-Pacing (<=15/Burst)
# und Chunk-Verlust/Re-Send-Netz.
#
# Aufruf: VOR jedem Release (VOR dem .gz-Pack), direkt nach check-build-number.sh.
# Exit != 0  => Release abbrechen. Läuft headless, keine Spiel-Interaktion noetig.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEST_DIR="$ROOT/tests/JobBoardSmokeTest"

if [[ ! -f "$TEST_DIR/JobBoardSmokeTest.csproj" ]]; then
  echo "[check-jobboard-sync] FAIL: Testprojekt nicht gefunden: $TEST_DIR" >&2
  exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "[check-jobboard-sync] FAIL: docker nicht verfuegbar — Sync-Gate kann nicht laufen." >&2
  exit 1
fi

echo "[check-jobboard-sync] Baue + starte JobBoardSmokeTest im Container (sdk:6.0)..."
docker run --rm \
  -v "$ROOT":/src \
  -w /src/tests/JobBoardSmokeTest \
  mcr.microsoft.com/dotnet/sdk:6.0 \
  dotnet run -c Release

rc=$?
if [[ $rc -ne 0 ]]; then
  echo "[check-jobboard-sync] FAIL (exit $rc) — Sync-Kette gebrochen, KEIN Release." >&2
  exit $rc
fi
echo "[check-jobboard-sync] OK: JobBoard-Sync-Kette gruenn."
