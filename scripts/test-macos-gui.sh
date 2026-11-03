#!/usr/bin/env bash
set -euo pipefail

# Startup coverage only: Spotify, permissions, input and visual behavior still
# require a real signed-in Mac. Never use or write the user's settings here.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
case "$(/usr/bin/uname -m)" in
  arm64) RID=osx-arm64 ;;
  x86_64) RID=osx-x64 ;;
  *) echo "Unsupported native architecture" >&2; exit 2 ;;
esac
APP="$ROOT/dist/macos/$RID/Toastify Reloaded.app/Contents/MacOS/ToastifyReloaded.Mac"
LOG="$ROOT/dist/macos-smoke-test.log"
CONFIG="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/toastify-macos-smoke.XXXXXX")"
PID=""
cleanup() {
  if [[ -n "$PID" ]] && kill -0 "$PID" 2>/dev/null; then
    kill -TERM "$PID" 2>/dev/null || true
    sleep 1
    kill -KILL "$PID" 2>/dev/null || true
  fi
  if [[ -n "$PID" ]]; then wait "$PID" 2>/dev/null || true; fi
  rm -rf "$CONFIG"
}
trap cleanup EXIT
test -x "$APP"

env TOASTIFY_RELOADED_CONFIG_DIR="$CONFIG" DOTNET_EnableDiagnostics=0 \
  "$APP" >"$LOG" 2>&1 &
PID=$!
sleep 10
if ! kill -0 "$PID" 2>/dev/null; then
  echo "FAIL: native app exited before the 10-second smoke interval" | tee -a "$LOG"
  cat "$LOG"
  exit 1
fi
echo "PASS: native $RID app stayed alive for 10 seconds" | tee -a "$LOG"
