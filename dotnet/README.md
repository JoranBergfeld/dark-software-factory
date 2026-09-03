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
- `tests/Dsf.FeatureCouncil.Tests` — conveyor semantics: station order and
  checkpointing, resume past completed stations, terminal runs never re-driven,
  per-station failures as audited error states, and the dry-run filing preview.

## Runtime host

`src/Dsf.Runtime` (`dsf-runtime`) is the deployed runtime entrypoint. Each verb
composes `RuntimeSettings` from the existing env var names first (naming every
unset requirement and exiting non-zero), then does its real work:

- `run --signal <path> [--dry-run]` — parses the signal and drives the Feature
  Council conveyor (`Dsf.FeatureCouncil.Conveyor`, stations `s1_triage` ..
  `s7_filing`), printing the finished run: status, evidence/proposal counts,
  station checkpoints and the audit trail. `--dry-run` stops deliberately at the
  filing station (`previewed`), reporting every issue it would have filed (title,
  labels, intent key) without creating any of them and without touching the
  filer. A run that fails a station is reported with the failing station as the
  cause and exits non-zero; a telemetry or persistence failure on the way out
  never displaces that cause. Without `--dry-run`, accepted proposals are filed
  as GitHub issues, idempotently: each proposal carries a durable intent key
  (scope fingerprint + source kind) that is stamped into the issue body and
  searched for before filing. Reaching the filing boundary with no filer wired
  fails the run — after stations S1..S6 have run and checkpointed.
- `sweep [--dry-run]` — reads the enabled source agent roster from the product's
  App Configuration store (`agents.<KIND>.enabled`, product label overriding the
  unlabelled default) and drives that scheduled run through the conveyor. An empty
  roster is a real, audited empty sweep, never an assumed one.
- `serve-orchestrator [--host --port --loop --interval]` — serves `GET /healthz`
  and `POST /run` (a conveyor dry-run over the posted signal payload). `--loop`
  additionally sweeps every `--interval` seconds (or `DSF_SWEEP_INTERVAL`, default
  300) for as long as the host serves.
- `serve-agent --kind <kind> [--host --port]` — serves one source agent's A2A card
  at `/.well-known/agent-card.json`. `POST /gather` reads the kind's configured
  upstream integration (`DSF_SOURCE_<KIND>_ENDPOINT`, optionally
  `DSF_SOURCE_<KIND>_TOKEN`) and answers with the evidence it found; an
  unconfigured kind answers `503` naming that setting and a failing upstream
  answers `502` with the reason. An unknown `--kind` is rejected by name.

### Runtime dependency composition

Every conveyor-driving verb composes its collaborators before running a line, and
fails naming each unset setting rather than running a line that can neither
gather, file, nor persist:

- **Source agents** — in-process by default: each known kind gathers directly
  from its configured upstream integration (`DSF_SOURCE_<KIND>_ENDPOINT`,
  optionally `DSF_SOURCE_<KIND>_TOKEN`) in the orchestrator's own process, no
  separately served agent required. A remote, served source agent is used
  instead only for a kind whose agent endpoint is explicitly configured
  (`DSF_SOURCE_AGENT_ENDPOINT_<KIND>` or `DSF_SOURCE_AGENT_ENDPOINT_TEMPLATE`, a
  base URL containing `{kind}`), gathered from over the same `/gather` protocol
  `serve-agent` serves. Either way, a kind whose upstream integration is
  unconfigured fails at `s2_investigation`, naming the kind and the setting.
- **Filing** — the GitHub REST filer, authenticated as the DSF GitHub App
  (`GITHUB_APP_ID`, `GITHUB_INSTALLATION_ID`, `GITHUB_APP_PRIVATE_KEY_SECRET`,
  `AZURE_KEYVAULT_URI`) and `GITHUB_REPOSITORY`; `DSF_GITHUB_API_URL` overrides
  the API base URL. There is no `GITHUB_TOKEN`/`GH_TOKEN` fallback in any
  environment — incomplete App settings fail the composition by name.
- **Persistence** — the run blackboard is upserted into Cosmos
  (`AZURE_COSMOS_ENDPOINT`, `DSF_COSMOS_DATABASE`/`DSF_COSMOS_CONTAINER`,
  defaulting to `dsf`/`runs`) after every station checkpoint, using the runtime's
  managed identity. A store that cannot be written to fails the run.
- **Model** — synthesis and council reason over evidence through a real Azure
  OpenAI chat completions deployment (`AZURE_OPENAI_ENDPOINT`,
  `AZURE_OPENAI_DEPLOYMENT`), authenticated with the runtime's managed identity.
  A failed completion fails the station it was called from.
- **Tracing** — every run and station boundary is reported to Application
  Insights (`APPLICATIONINSIGHTS_CONNECTION_STRING`). A tracing failure is
  audited on the run but never fails it — telemetry reachability must never
  decide whether a line that did its work is reported as having failed.

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
