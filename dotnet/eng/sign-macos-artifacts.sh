#!/usr/bin/env bash
set -euo pipefail

artifact_root="${1:?artifact root required}"

: "${MACOS_DEVELOPER_ID_CERTIFICATE_BASE64:?macOS signing certificate is required}"
: "${MACOS_DEVELOPER_ID_CERTIFICATE_PASSWORD:?macOS signing certificate password is required}"
: "${MACOS_NOTARYTOOL_PROFILE:?macOS notarytool keychain profile is required}"

certificate_path="$artifact_root/macos-developer-id.p12"
printf '%s' "$MACOS_DEVELOPER_ID_CERTIFICATE_BASE64" | base64 --decode > "$certificate_path"
security import "$certificate_path" -P "$MACOS_DEVELOPER_ID_CERTIFICATE_PASSWORD" -T /usr/bin/codesign

find "$artifact_root" -type f -name dsf -print0 | while IFS= read -r -d '' executable; do
  codesign --force --options runtime --timestamp --sign "Developer ID Application" "$executable"
  ditto -c -k --keepParent "$executable" "$executable.notarize.zip"
  xcrun notarytool submit "$executable.notarize.zip" --keychain-profile "$MACOS_NOTARYTOOL_PROFILE" --wait
  rm -f "$executable.notarize.zip"
done

rm -f "$certificate_path"
