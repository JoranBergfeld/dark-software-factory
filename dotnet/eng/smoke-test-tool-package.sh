#!/usr/bin/env bash
set -euo pipefail

package="$(cd "$(dirname "${1:?NuGet package required}")" && pwd)/$(basename "$1")"
eng="$(cd "$(dirname "$0")" && pwd)"
if [[ -n "${DSF_SMOKE_DIRECTORY:-}" ]]; then
  test ! -e "$DSF_SMOKE_DIRECTORY"
  mkdir -p "$DSF_SMOKE_DIRECTORY"
  scratch="$(cd "$DSF_SMOKE_DIRECTORY" && pwd)"
  export DSF_INSTALLED_SMOKE_DIRECTORY="$scratch/verification"
else
  scratch="$(mktemp -d)"
  trap 'rm -rf "$scratch"' EXIT
fi
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
