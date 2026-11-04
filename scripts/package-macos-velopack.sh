#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"
case "$RID" in osx-x64|osx-arm64) ;; *) echo "Usage: $0 osx-x64|osx-arm64" >&2; exit 2 ;; esac
[[ "$(/usr/bin/uname -s)" == "Darwin" ]] || { echo "Velopack macOS packaging requires a Mac." >&2; exit 1; }

VPK="${VPK_PATH:-$ROOT/.tools/vpk/vpk}"
[[ -x "$VPK" ]] || { echo "Install vpk 1.2.161 in .tools/vpk." >&2; exit 1; }
"$VPK" --help | /usr/bin/grep -Fq 'Velopack CLI 1.2.161' || { echo "Wrong Velopack CLI version." >&2; exit 1; }
APP="$ROOT/dist/macos/$RID/Toastify Reloaded.app"
OUT="$ROOT/dist/velopack/$RID"
[[ -x "$APP/Contents/MacOS/ToastifyReloaded.Mac" ]] || { echo "Prebuilt app bundle missing: $APP" >&2; exit 1; }

VERSION="$(python3 - "$ROOT" <<'PY'
from pathlib import Path
import re, sys
root = Path(sys.argv[1])
source = (root / 'src/ToastifyReloaded.Mac/Services/MacUpdateService.cs').read_text(encoding='utf-8-sig')
tag = re.search(r'public const string CurrentTag = "([^"]+)";', source).group(1)
match = re.fullmatch(r'v(\d+\.\d+\.\d+)-macos(?:-(preview|rc)\.([1-9][0-9]*))?', tag)
if not match: raise SystemExit('Invalid macOS release tag')
print(match[1] + ('-' + match[2] + '.' + match[3] if match[2] else ''))
PY
)"

# Destructive operations are limited to resolved build outputs in this checkout.
python3 - "$ROOT" "$OUT" "$APP" <<'PY'
from pathlib import Path
import sys
root = (Path(sys.argv[1]) / 'dist').resolve()
for argument in sys.argv[2:]:
    target = Path(argument).resolve()
    if target == root or not target.is_relative_to(root): raise SystemExit('Unsafe packaging output path')
PY
rm -rf "$OUT"
mkdir -p "$OUT"
python3 "$ROOT/scripts/portable-release.py" --platform macos --version "$VERSION" \
    --directory "$ROOT/dist" --prepare-base --output "$OUT" --runtime "$RID"

SIGNING_IDENTITY="${MACOS_SIGNING_IDENTITY:--}"
# Exercise the pinned SDK's exact signing flags before it creates package hashes.
# Apple ad-hoc signatures have no CMS timestamp; native CI verifies acceptance.
if [[ "$SIGNING_IDENTITY" == '-' ]]; then
    SIGN_PROBE="$OUT/.codesign-preflight"
    cp /usr/bin/true "$SIGN_PROBE"
    if /usr/bin/codesign -s - -f -v --timestamp --options runtime \
        --entitlements "$ROOT/packaging/macos/ToastifyReloaded.entitlements" "$SIGN_PROBE"; then
        /usr/bin/codesign --verify --strict "$SIGN_PROBE"
        rm -f "$SIGN_PROBE"
    else
        rm -f "$SIGN_PROBE"
        echo 'Native ad-hoc signing preflight failed; no update feed was produced.' >&2
        exit 1
    fi
fi
ARGS=(pack --packId ToastifyReloadedMac --packVersion "$VERSION" --packTitle "Toastify Reloaded"
      --packAuthors MarliusLab --packDir "$APP" --mainExe ToastifyReloaded.Mac
      --runtime "$RID" --channel "$RID" --outputDir "$OUT" --noInst --delta BestSize
      --signAppIdentity "$SIGNING_IDENTITY" --signDisableDeep
      --signEntitlements "$ROOT/packaging/macos/ToastifyReloaded.entitlements")
TAG="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["macos"]["tag"])' "$ROOT/docs/releases/release-plan.json")"
NOTES="$ROOT/docs/releases/$TAG.md"
[[ -s "$NOTES" ]] || { echo "Version-specific notes missing: $NOTES" >&2; exit 1; }
ARGS+=(--releaseNotes "$NOTES")
"$VPK" "${ARGS[@]}"

PORTABLE=""
while IFS= read -r -d '' candidate; do
    [[ -z "$PORTABLE" ]] || { echo "Ambiguous portable output in $OUT" >&2; exit 1; }
    PORTABLE="$candidate"
done < <(/usr/bin/find "$OUT" -maxdepth 1 -type f -name '*Portable.zip' -print0)
[[ -n "$PORTABLE" ]] || { echo "Velopack portable ZIP missing." >&2; exit 1; }
STAGE="$(mktemp -d "$ROOT/dist/macos/$RID/vpk-portable.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
/usr/bin/ditto -x -k "$PORTABLE" "$STAGE"
NEW_APP="$STAGE/Toastify Reloaded.app"
[[ -d "$NEW_APP" && -x "$NEW_APP/Contents/MacOS/UpdateMac" && -s "$NEW_APP/Contents/Resources/sq.version" ]] || {
    echo "Velopack portable lacks its update helper or manifest." >&2; exit 1;
}
/usr/bin/plutil -lint "$NEW_APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$NEW_APP/Contents/Info.plist" | /usr/bin/grep -Fxq 'io.github.Marlius92.ToastifyReloaded'
/usr/libexec/PlistBuddy -c 'Print :NSAppleEventsUsageDescription' "$NEW_APP/Contents/Info.plist" | /usr/bin/grep -Fq 'Spotify'
/usr/bin/codesign --verify --strict --verbose=2 "$NEW_APP/Contents/MacOS/UpdateMac"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$NEW_APP"

# ZIP/DMG scripts and startup smoke tests consume this finalized, signed bundle.
rm -rf "$APP"
/usr/bin/ditto "$NEW_APP" "$APP"
printf '%s\n' "$VERSION" > "$ROOT/dist/macos/package-version.txt"
for kind in full delta; do
    package="$OUT/ToastifyReloadedMac-$VERSION-$RID-$kind.nupkg"
    [[ ! -f "$package" ]] || cp "$package" "$ROOT/dist/"
done
cp "$OUT/releases.$RID.json" "$ROOT/dist/"
[[ ! -f "$OUT/RELEASES-$RID" ]] || cp "$OUT/RELEASES-$RID" "$ROOT/dist/"
python3 "$ROOT/scripts/portable-release.py" --platform macos --version "$VERSION" \
    --directory "$ROOT/dist" --finalize --runtime "$RID"
echo "PASS: Velopack $VERSION $RID bundle, update helper, feed and packages"
