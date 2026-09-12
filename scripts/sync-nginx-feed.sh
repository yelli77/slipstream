#!/usr/bin/env bash
# sync-nginx-feed.sh: Kopiert version.json + alle builds/*.dll.gz in den nginx-Container-HTML-Ordner.
# Idempotent; HTML-Pfad wird dynamisch per docker inspect ermittelt, fallback docker cp.
set -u
CONTAINER="${NGINX_CONTAINER:-starttruckmp-starttruckmp-1}"
SRC_DIR="$(cd "$(dirname "$0")/.." && pwd)"
FEED_URL="https://raw.githubusercontent.com/yelli77/slipstream/main/builds/StarTruckMP-custom-build-306.dll.gz"

# version.json in src erzeugen (falls nicht aktuell) — Build-Nummer aus neuestem builds/*.dll.gz
LATEST_GZ="$(ls -1 "$SRC_DIR"/builds/StarTruckMP-custom-build-*.dll.gz 2>/dev/null | sed "s/.*custom-build-//;s/\\.dll\\.gz//" | sort -n | tail -1)"
if [ -n "${LATEST_GZ}" ]; then
  BUILD="custom-build-${LATEST_GZ}"
  URL="https://raw.githubusercontent.com/yelli77/slipstream/main/builds/StarTruckMP-${BUILD}.dll.gz"
  python3 -c "
import json, sys
with open(\"$SRC_DIR/version.json\", \"w\") as f:
    json.dump({\"build\": \"$BUILD\", \"url\": \"$URL\"}, f)
print(f\"[sync-nginx-feed] version.json geschrieben: {\"$BUILD\"}\")
"
else
  echo "[sync-nginx-feed] WARN: keine builds/*.dll.gz gefunden, version.json unveraendert" >&2
fi

# Ziel-Pfad ermitteln: Mount auf /usr/share/nginx/html suchen
HTML_PATH="$(docker inspect "$CONTAINER" --format "{{range .Mounts}}{{if eq .Destination \"/usr/share/nginx/html\"}}{{.Source}}{{end}}{{end}}" 2>/dev/null)"

if [ -n "$HTML_PATH" ] && [ -d "$HTML_PATH" ]; then
  cp -f "$SRC_DIR/version.json" "$HTML_PATH/version.json"
  cp -f "$SRC_DIR"/builds/*.dll.gz "$HTML_PATH/" 2>/dev/null || true
  echo "[sync-nginx-feed] Via Mount kopiert: $HTML_PATH"
else
  echo "[sync-nginx-feed] Kein Mount gefunden, fallback: docker cp"
  docker cp "$SRC_DIR/version.json" "$CONTAINER:/usr/share/nginx/html/version.json"
  for f in "$SRC_DIR"/builds/*.dll.gz; do
    docker cp "$f" "$CONTAINER:/usr/share/nginx/html/$(basename "$f")"
  done
fi

echo "[sync-nginx-feed] Done."
exit 0
