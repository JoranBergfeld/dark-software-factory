#!/usr/bin/env bash
set -euo pipefail

artifact_dir="${1:?artifact directory required}"
rid="${2:?rid required}"

case "$rid" in
  win-*) exe="$artifact_dir/dsf.exe" ;;
  *) exe="$artifact_dir/dsf" ;;
esac

if [[ ! -f "$exe" ]]; then
  echo "missing CLI executable for $rid: $exe" >&2
  exit 1
fi

chmod +x "$exe" 2>/dev/null || true

host_os="$(uname -s | tr '[:upper:]' '[:lower:]')"
host_arch="$(uname -m)"
can_execute=false

case "$rid:$host_os:$host_arch" in
  linux-x64:linux:x86_64) can_execute=true ;;
  linux-arm64:linux:aarch64) can_execute=true ;;
  osx-x64:darwin:x86_64) can_execute=true ;;
  osx-arm64:darwin:arm64) can_execute=true ;;
  win-x64:mingw*:x86_64|win-x64:msys*:x86_64|win-x64:cygwin*:x86_64) can_execute=true ;;
  win-arm64:mingw*:aarch64|win-arm64:msys*:aarch64|win-arm64:cygwin*:aarch64) can_execute=true ;;
esac

if [[ "$can_execute" == "true" ]]; then
  "$exe" --help >/dev/null
else
  test -s "$exe"
  echo "metadata smoke passed for non-native $rid on $host_os/$host_arch"
fi
