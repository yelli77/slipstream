#!/usr/bin/env bash
# sync-nginx-feed.sh: Kopiert version.json + alle builds/*.dll.gz in den nginx-Container-HTML-Ordner.
# Idempotent; HTML-Pfad wird dynamisch per docker inspect ermittelt, fallback docker cp.
set -u
CONTAINER="${NGINX_CONTAINER:-starttruckmp-starttruckmp-1}"
SRC_DIR="$(cd "$(dirname "$0")/.." && pwd)"

# version.json in src wird NICHT mehr automatisch ueberschrieben —
# sie ist die authoritative Quelle (architect-gesteuert). Nur Kopieren nach nginx.
python3 -c "import json; json.load(open(\"$SRC_DIR/version.json\"))" || { echo "[sync-nginx-feed] ERROR: version.json fehlt/invalid - abort" >&2; exit 1; }
BUILD="$(python3 -c "import json; print(json.load(open(\"$SRC_DIR/version.json\"))[\"build\"])")"
echo "[sync-nginx-feed] Feed-Stand laut version.json: $BUILD"
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
