#!/usr/bin/env bash
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: ./scripts/package.sh <version> [changelog] [--force]

Examples:
  ./scripts/package.sh 0.1.1 "Improve generated caption session cleanup"
  ./scripts/package.sh 0.1.1 "Improve generated caption session cleanup" --force

Rules:
  - version must be x.y.z
  - manifest version is written as x.y.z.0
  - lower-than-latest versions are rejected
  - existing versions/packages are rejected unless --force is passed
EOF
}

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/Jellyfin.Plugin.AutoGenerateCaptions.csproj"
PLUGIN_DLL="Jellyfin.Plugin.AutoGenerateCaptions.dll"
MANIFEST="$ROOT_DIR/manifest.json"
TARGET_ABI="10.11.0.0"
REPO_RAW_BASE="https://raw.githubusercontent.com/naqadata/jellyfin-plugin-auto-generate-captions/main/dist"

VERSION="${1:-}"
CHANGELOG="${2:-Release ${VERSION}}"
FORCE=false

for arg in "$@"; do
    if [ "$arg" = "--force" ]; then
        FORCE=true
    fi
done

if [ -z "$VERSION" ] || [ "$VERSION" = "-h" ] || [ "$VERSION" = "--help" ]; then
    usage
    exit 0
fi

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Version must be in x.y.z format, got: $VERSION" >&2
    exit 1
fi

if [ "$CHANGELOG" = "--force" ]; then
    CHANGELOG="Release ${VERSION}"
fi

require_tool() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "Missing required tool: $1" >&2
        exit 1
    fi
}

version_lt() {
    [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -n1)" = "$1" ] && [ "$1" != "$2" ]
}

require_tool dotnet
require_tool jq
require_tool md5sum
require_tool zip

MANIFEST_VERSION="${VERSION}.0"
PACKAGE_NAME="Jellyfin.Plugin.AutoGenerateCaptions_${VERSION}.zip"
PACKAGE_PATH="$ROOT_DIR/dist/$PACKAGE_NAME"
SOURCE_URL="${REPO_RAW_BASE}/${PACKAGE_NAME}"
TIMESTAMP="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"

cd "$ROOT_DIR"

latest_version="$(jq -r '.[0].versions[].version' "$MANIFEST" | sort -V | tail -1)"
if [ -n "$latest_version" ] && version_lt "$MANIFEST_VERSION" "$latest_version"; then
    echo "Refusing to package $MANIFEST_VERSION because latest manifest version is $latest_version" >&2
    exit 1
fi

manifest_has_version=false
if jq -e --arg version "$MANIFEST_VERSION" '.[0].versions[] | select(.version == $version)' "$MANIFEST" >/dev/null; then
    manifest_has_version=true
fi

if { [ -e "$PACKAGE_PATH" ] || [ "$manifest_has_version" = true ]; } && [ "$FORCE" != true ]; then
    echo "Refusing to overwrite existing release artifacts for $VERSION." >&2
    echo "Use --force only before the version has been pushed/released." >&2
    exit 1
fi

rm -rf "$ROOT_DIR/package"
dotnet build "$PROJECT" -c Release

mkdir -p "$ROOT_DIR/package" "$ROOT_DIR/dist"
cp "$ROOT_DIR/bin/Release/net9.0/$PLUGIN_DLL" "$ROOT_DIR/package/"

if [ -e "$PACKAGE_PATH" ] && [ "$FORCE" = true ]; then
    rm -f "$PACKAGE_PATH"
fi

(
    cd "$ROOT_DIR/package"
    zip -X -9 "../dist/$PACKAGE_NAME" "$PLUGIN_DLL"
)

checksum="$(md5sum "$PACKAGE_PATH" | awk '{print $1}')"

tmp_manifest="$(mktemp)"
jq \
    --arg version "$MANIFEST_VERSION" \
    --arg changelog "$CHANGELOG" \
    --arg targetAbi "$TARGET_ABI" \
    --arg sourceUrl "$SOURCE_URL" \
    --arg checksum "$checksum" \
    --arg timestamp "$TIMESTAMP" \
    --argjson force "$FORCE" \
    '
    .[0].versions as $versions
    | ($versions | map(.version == $version) | any) as $exists
    | if $exists then
        if $force then
          .[0].versions |= map(if .version == $version then
            . + {
              changelog: $changelog,
              targetAbi: $targetAbi,
              sourceUrl: $sourceUrl,
              checksum: $checksum,
              timestamp: $timestamp
            }
          else . end)
        else
          error("version already exists")
        end
      else
        .[0].versions += [{
          version: $version,
          changelog: $changelog,
          targetAbi: $targetAbi,
          sourceUrl: $sourceUrl,
          checksum: $checksum,
          timestamp: $timestamp
        }]
      end
    ' "$MANIFEST" > "$tmp_manifest"
mv "$tmp_manifest" "$MANIFEST"

echo "Wrote $PACKAGE_PATH"
echo "Manifest version $MANIFEST_VERSION"
echo "MD5 $checksum"
