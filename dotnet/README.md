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

## Runtime host

`src/Dsf.Runtime` (`dsf-runtime`) is the deployed runtime entrypoint. Each verb
composes `RuntimeSettings` from the existing env var names first (naming every
unset requirement and exiting non-zero), then does its real work:

- `run --signal <path> [--dry-run]` — parses the signal and drives the Feature
  Council conveyor (`Dsf.FeatureCouncil.Conveyor`, stations `s1_triage` ..
  `s7_filing`), printing the finished run: status, evidence/proposal counts,
  station checkpoints and the audit trail. `--dry-run` stops deliberately at the
  filing station (`previewed`). Without `--dry-run`, a run with accepted proposals
  fails at the filing boundary until the GitHub issue filer lands (#143) — after
  stations S1..S6 have run and checkpointed.
- `sweep [--dry-run]` — reads the enabled source agent roster from the product's
  App Configuration store (`agents.<KIND>.enabled`, product label overriding the
  unlabelled default) and drives that scheduled run through the conveyor. An empty
  roster is a real, audited empty sweep, never an assumed one.
- `serve-orchestrator [--host --port --loop --interval]` — serves `GET /healthz`
  and `POST /run` (a conveyor dry-run over the posted signal payload). `--loop`
  additionally sweeps every `--interval` seconds (or `DSF_SWEEP_INTERVAL`, default
  300) for as long as the host serves.
- `serve-agent --kind <kind> [--host --port]` — serves one source agent's A2A card
  at `/.well-known/agent-card.json`. `POST /gather` answers `501` naming the
  missing source connector until #144 lands; an unknown `--kind` is rejected by
  name.

The `dsf` front door (`src/Dsf.Cli`) forwards these verbs to the `dsf-runtime`
executable as a child process — the same way the Python front door shells out to
`python -m dsf.runtime.control` — so it never needs to reference the runtime
module. It resolves the executable next to itself, or from `DSF_RUNTIME_HOST`.

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
