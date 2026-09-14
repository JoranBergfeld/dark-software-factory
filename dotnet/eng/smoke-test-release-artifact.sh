#!/usr/bin/env bash
set -euo pipefail

artifact_dir="$(cd "${1:?artifact directory required}" && pwd)"
rid="${2:?rid required}"
eng="$(cd "$(dirname "$0")" && pwd)"

case "$rid" in
  win-*) exe="$artifact_dir/dsf.exe"; runtime="$artifact_dir/runtime/dsf-runtime.exe" ;;
  *) exe="$artifact_dir/dsf"; runtime="$artifact_dir/runtime/dsf-runtime" ;;
esac

if [[ ! -f "$exe" ]]; then
  echo "missing CLI executable for $rid: $exe" >&2
  exit 1
fi

test -s "$runtime"
chmod +x "$exe" "$runtime"
for template in owner-keyvault owner-secrets main sre-agent copy-owner-secret; do
  test -s "$artifact_dir/assets/infra/$template.json"
done

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
  win-arm64:mingw*:arm64|win-arm64:msys*:arm64|win-arm64:cygwin*:arm64) can_execute=true ;;
esac

if [[ "$can_execute" == "true" ]]; then
  dotnet run --project "$eng/InstallationSmoke" -- --cli "$exe" --payload "$artifact_dir"
else
  test -s "$exe"
  echo "metadata smoke passed for non-native $rid on $host_os/$host_arch"
fi
