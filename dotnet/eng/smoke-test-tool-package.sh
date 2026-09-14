#!/usr/bin/env bash
set -euo pipefail

package="$(cd "$(dirname "${1:?NuGet package required}")" && pwd)/$(basename "$1")"
eng="$(cd "$(dirname "$0")" && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
name="$(basename "$package")"
version="${name#DarkSoftwareFactory.Cli.}"
version="${version%.nupkg}"
printf '<configuration><packageSources><clear /></packageSources></configuration>' > "$scratch/NuGet.Config"
(
  cd "$scratch"
  NUGET_PACKAGES="$scratch/packages" dotnet tool install DarkSoftwareFactory.Cli \
    --version "$version" --tool-path "$scratch/tool" \
    --configfile "$scratch/NuGet.Config" --add-source "$(dirname "$package")"
)
cli="$scratch/tool/dsf"
if [[ -f "$cli.exe" ]]; then cli="$cli.exe"; fi
assembly="$(find "$scratch/tool/.store" -type f -name dsf.dll -print -quit)"
test -n "$assembly"
test "$(cat "$(dirname "$assembly")/dsf-bundle.version")" = "$version"
dotnet run --project "$eng/InstallationSmoke" -- \
  --cli "$cli" --payload "$(dirname "$assembly")"
