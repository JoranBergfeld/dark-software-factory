# .NET solution skeleton

Migration-branch spine for the Dark Software Factory rewrite. See
`docs/adr/` and `docs/site/concept/` for the target architecture; this
directory holds the buildable seams only (no behavior migrated yet).

## Layout

- `src/Dsf.Core` — shared core library. References no application module.
- `src/Dsf.FeatureCouncil` — Feature Council library. References `Dsf.Core` only.
- `src/Dsf.Cli` — CLI executable (factory/provisioning + runtime-verb forwarding).
  References `Dsf.Core` only — no direct dependency on Feature Council internals.
- `src/Dsf.Runtime` — runtime executable (run/sweep/serve-orchestrator/serve-agent).
  References `Dsf.Core` + `Dsf.FeatureCouncil`.
- `src/Dsf.ControlCenter` — governance web executable (ASP.NET Core). References
  `Dsf.Core` only.
- `src/Dsf.AgentHost` — optional reusable agent host executable. References
  `Dsf.Core` + `Dsf.FeatureCouncil`.
- `src/Dsf.Testing` — deterministic doubles/test builders. References `Dsf.Core`
  only; **no production project may reference this module**.
- `tests/Dsf.ModuleBoundaries.Tests` — parses every `src/*.csproj` and asserts the
  allowed `ProjectReference` set per module, and that no production project
  references `Dsf.Testing`.
- `tests/Dsf.Core.Tests` — example test project demonstrating a test using both
  `Dsf.Core` and `Dsf.Testing`.

## Dependency management

- `global.json` pins the SDK (`10.0.301`, `rollForward: latestPatch`, no
  prerelease).
- `Directory.Packages.props` enables Central Package Management with exact,
  pinned direct dependency versions.
- Each project restores with a committed `packages.lock.json`
  (`RestorePackagesWithLockFile`); CI restores with `--locked-mode`.
- `NuGet.config` maps all packages to `nuget.org` only.
- `Directory.Build.props` enables `NuGetAudit` (direct dependencies, `low`
  severity floor).

## Common commands

```bash
cd dotnet
dotnet restore Dsf.sln --locked-mode
dotnet build Dsf.sln --no-restore
dotnet test Dsf.sln --no-build
dotnet list Dsf.sln package --vulnerable --include-transitive
```
