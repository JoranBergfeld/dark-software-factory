#!/usr/bin/env bash
set -euo pipefail

version="v0.44.1"
case "$(uname -s)" in
  Linux) platform=linux ;;
  Darwin) platform=osx ;;
  MINGW*|MSYS*|CYGWIN*) platform=win ;;
  *) echo "Unsupported Bicep build host" >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64|amd64) arch=x64 ;;
  aarch64|arm64) arch=arm64 ;;
  *) echo "Unsupported Bicep build architecture" >&2; exit 1 ;;
esac
suffix=""
if [[ "$platform" == win ]]; then suffix=".exe"; fi
destination="${RUNNER_TEMP:?RUNNER_TEMP required}/dsf-build-tools"
mkdir -p "$destination"
curl --fail --silent --show-error --location \
  "https://github.com/Azure/bicep/releases/download/$version/bicep-$platform-$arch$suffix" \
  --output "$destination/bicep$suffix"
chmod +x "$destination/bicep$suffix"
"$destination/bicep$suffix" --version
echo "$destination" >> "${GITHUB_PATH:?GITHUB_PATH required}"
