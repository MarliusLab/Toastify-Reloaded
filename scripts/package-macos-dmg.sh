#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"

case "$RID" in
  osx-x64) ARCH=x64 ;;
  osx-arm64) ARCH=arm64 ;;
  *)
    echo "Usage: $0 osx-x64|osx-arm64" >&2
    exit 2
    ;;
esac

APP="$ROOT/dist/macos/$RID/Toastify Reloaded.app"
OUT="$ROOT/dist/ToastifyReloaded-macOS-$ARCH.dmg"
STAGE="$ROOT/dist/macos/$RID/dmg-stage"
[[ -d "$APP" ]] || { echo "App bundle missing: $APP" >&2; exit 1; }

rm -rf "$STAGE" "$OUT"
mkdir -p "$STAGE"
cleanup() {
  local result=$?
  rm -rf "$STAGE"
  if [[ "$result" -ne 0 ]]; then rm -f "$OUT"; fi
}
trap cleanup EXIT
/usr/bin/ditto "$APP" "$STAGE/Toastify Reloaded.app"
/bin/ln -s /Applications "$STAGE/Applications"

# Automatic image sizing can leave too little filesystem space for this
# bundle on current macOS runners. Reserve explicit space for HFS+ metadata
# and copying; UDZO compression omits the unused space from the final file.
PAYLOAD_KB="$(/usr/bin/du -sk "$STAGE" | /usr/bin/awk '{print $1}')"
IMAGE_MB=$(( (PAYLOAD_KB * 5 / 4 + 1023) / 1024 + 64 ))
if [[ "$IMAGE_MB" -lt 128 ]]; then IMAGE_MB=128; fi
echo "DMG payload: ${PAYLOAD_KB} KiB; image capacity: ${IMAGE_MB} MiB"
/bin/df -Pk "$ROOT/dist" "${TMPDIR:-/tmp}"

/usr/bin/hdiutil create \
  -volname "Toastify Reloaded" \
  -srcfolder "$STAGE" \
  -ov \
  -format UDZO \
  -fs HFS+ \
  -size "${IMAGE_MB}m" \
  "$OUT" >/dev/null

/usr/bin/hdiutil verify "$OUT" >/dev/null
echo "Created: $OUT"
