#!/usr/bin/env bash
set -euo pipefail

archive="$(cd "$(dirname "${1:?release archive required}")" && pwd)/$(basename "$1")"
rid="${2:?rid required}"
eng="$(cd "$(dirname "$0")" && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
case "$archive" in
  *.tar.gz) tar -xzf "$archive" -C "$scratch" ;;
  *.zip) unzip -q "$archive" -d "$scratch" ;;
  *) echo "Unsupported release archive: $archive" >&2; exit 1 ;;
esac
"$eng/smoke-test-release-artifact.sh" "$scratch" "$rid"
