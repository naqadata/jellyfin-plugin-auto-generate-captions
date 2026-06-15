#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/Jellyfin.Plugin.AutoGenerateCaptions.csproj"
VERSION="0.1.0"
PACKAGE_NAME="Jellyfin.Plugin.AutoGenerateCaptions_${VERSION}.zip"
PACKAGE_PATH="$ROOT_DIR/dist/$PACKAGE_NAME"
MANIFEST="$ROOT_DIR/manifest.json"

cd "$ROOT_DIR"

rm -rf "$ROOT_DIR/package"
dotnet build "$PROJECT" -c Release

mkdir -p "$ROOT_DIR/package" "$ROOT_DIR/dist"
cp "$ROOT_DIR/bin/Release/net9.0/Jellyfin.Plugin.AutoGenerateCaptions.dll" "$ROOT_DIR/package/"
touch -t 202606140000 "$ROOT_DIR/package/Jellyfin.Plugin.AutoGenerateCaptions.dll"
rm -f "$PACKAGE_PATH"

(
    cd "$ROOT_DIR/package"
    zip -X -9 "../dist/$PACKAGE_NAME" Jellyfin.Plugin.AutoGenerateCaptions.dll
)

checksum="$(md5sum "$PACKAGE_PATH" | awk '{print $1}')"
perl -0pi -e "s/\"checksum\": \"[0-9a-fA-F]{32}\"/\"checksum\": \"$checksum\"/" "$MANIFEST"

echo "Wrote $PACKAGE_PATH"
echo "MD5 $checksum"
