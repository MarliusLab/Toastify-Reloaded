#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARCH="${1:-x64}"
VERSION="${2:-1.4.2}"
case "$ARCH" in x64|arm64) ;; *) echo "Unsupported Linux architecture: $ARCH" >&2; exit 1 ;; esac
[[ "$(uname -s)" == Linux ]] || { echo "Velopack AppImages must be packaged on Linux." >&2; exit 1; }
RUNTIME="linux-$ARCH"
PUBLISH="$ROOT/dist/$RUNTIME"
OUTPUT="$ROOT/dist/velopack/$RUNTIME"
VPK="${TOASTIFY_VPK:-$ROOT/.tools/vpk/vpk}"
test -x "$PUBLISH/ToastifyReloaded.Linux"
test -x "$VPK"
mkdir -p "$OUTPUT"

python3 "$ROOT/scripts/portable-release.py" --platform linux --version "$VERSION" \
  --directory "$ROOT/dist" --runtime "$RUNTIME" --prepare-base --output "$OUTPUT"
"$VPK" --skip-updates --yes pack \
  --packId ToastifyReloadedLinux --packTitle ToastifyReloaded \
  --packVersion "$VERSION" --packDir "$PUBLISH" --mainExe ToastifyReloaded.Linux \
  --channel "$RUNTIME" --runtime "$RUNTIME" --outputDir "$OUTPUT" \
  --icon "$ROOT/packaging/linux/io.github.Marlius92.ToastifyReloaded.png"

FULL="$OUTPUT/ToastifyReloadedLinux-$VERSION-$RUNTIME-full.nupkg"
DELTA="$OUTPUT/ToastifyReloadedLinux-$VERSION-$RUNTIME-delta.nupkg"
test -s "$FULL"
cp "$FULL" "$ROOT/dist/"
if [[ -s "$DELTA" ]]; then cp "$DELTA" "$ROOT/dist/"; fi
cp "$OUTPUT/releases.$RUNTIME.json" "$ROOT/dist/"
if [[ -f "$OUTPUT/RELEASES-$RUNTIME" ]]; then cp "$OUTPUT/RELEASES-$RUNTIME" "$ROOT/dist/"; fi
mapfile -t IMAGES < <(find "$OUTPUT" -maxdepth 1 -type f -name '*.AppImage' -print)
[[ "${#IMAGES[@]}" -eq 1 ]] || { echo "Expected exactly one generated AppImage." >&2; exit 1; }
cp "${IMAGES[0]}" "$ROOT/dist/ToastifyReloaded-Linux-$ARCH.AppImage"
chmod +x "$ROOT/dist/ToastifyReloaded-Linux-$ARCH.AppImage"
python3 "$ROOT/scripts/portable-release.py" --platform linux --version "$VERSION" \
  --directory "$ROOT/dist" --runtime "$RUNTIME" --finalize
