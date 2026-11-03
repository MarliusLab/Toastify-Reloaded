#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$ROOT/dist/linux-x64/ToastifyReloaded.Linux"
TMP_CONFIG="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/toastify-linux-selftest.XXXXXX")"
LOG="$ROOT/dist/linux-release-self-test.log"
trap 'rm -rf "$TMP_CONFIG"' EXIT

test -x "$APP"

echo "=== Toastify Reloaded Linux stable headless self-test ===" | tee "$LOG"

env \
  XDG_CONFIG_HOME="$TMP_CONFIG" \
  XDG_SESSION_TYPE=x11 \
  TOASTIFY_DISABLE_NATIVE_WAYLAND=1 \
  "$APP" --self-test \
  2>&1 | tee -a "$LOG"

grep -q 'SELF-TEST RESULT: PASS' "$LOG"

echo "Stable headless self-test passed."
