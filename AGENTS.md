# Coding agent instructions — Dark Software Factory (DSF)

DSF is a **blueprint**, not a running factory: a template plus tooling that stamps out an
isolated software factory per product (decide what to build → build it → operate it), with
people governing from outside the loop.

## Commands

Work from `dotnet/` and use the .NET SDK.

- Restore: `dotnet restore Dsf.sln --locked-mode`
- Build: `dotnet build Dsf.sln --no-restore`
- Test all: `dotnet test Dsf.sln --no-build`
- Single test project: `dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --no-build`
- Single test: `dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter FullyQualifiedName~TestName --no-build`
- Pack tool: `dotnet pack src/Dsf.Cli/Dsf.Cli.csproj -c Release -o artifacts/release/nuget`
- Publish native CLI: `dotnet publish src/Dsf.Cli/Dsf.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o artifacts/release/linux-x64`

CI for current code is `.github/workflows/dotnet-ci.yml`; release automation is
`.github/workflows/dotnet-release.yml`.

## Workspace layout

A .NET solution (`dotnet/Dsf.sln`) with these projects:

- `Dsf.Core` — shared contracts, runtime settings, product records, and charters.
- `Dsf.FeatureCouncil` — Feature Council conveyor, stations, and council logic.
- `Dsf.Cli` — packaged `dsf` global tool: factory provisioning, charter commands, and runtime forwarding.
- `Dsf.Runtime` — deployed runtime host: `run`, `sweep`, `serve-orchestrator`, `serve-agent`, `poll-outcomes`.
- `Dsf.ControlCenter` — governance web surface.
- `Dsf.AgentHost` — reusable source-agent host.
- `Dsf.Testing` — deterministic test helpers; production projects must not reference it.
- `tests/*` — xUnit suites, including module-boundary tests.

**Import rule:** `Dsf.Core` references no application module. Application modules do not
reference each other except `Dsf.Runtime`/`Dsf.AgentHost` may reference `Dsf.FeatureCouncil`.
`Dsf.Testing` is test-only. `Dsf.ModuleBoundaries.Tests` gates this.

## Architecture

### Conveyor

`dotnet/src/Dsf.FeatureCouncil/Conveyor/ConveyorLine.cs` drives stations S1..S7 over a run:

1. `S1Triage` — debounce/dedup; can kill a run.
2. `S2Investigation` — gather evidence from source agents.
3. `S3Synthesis` — turn evidence into proposals.
4. `S4Grounding` — trace claims to evidence.
5. `S5Council` — critics deliberate and vote.
6. `S6Routing` — apply labels and routing policy.
7. `S7Filing` — file de-duplicated GitHub issues; dry-run previews only.

Each station records an idempotent checkpoint. Terminal runs are not re-driven. Station
exceptions become audited error states.

### Runtime and ports

External dependencies are real Azure/GitHub implementations composed by `Dsf.Runtime`:
App Configuration, Cosmos, Key Vault, Azure OpenAI, Application Insights, and the DSF GitHub
App. Missing settings fail loudly by name; do not add offline fallbacks to production code.
Deterministic doubles belong in `Dsf.Testing` or test projects.

### Entry points

- `dsf new` — provision an isolated product factory.
- `dsf bootstrap` — create owner GitHub App, Key Vault, and App Configuration.
- `dsf charter ...` — manage product intent.
- `dsf run|sweep|serve-orchestrator|serve-agent|poll-outcomes` — forwarded runtime verbs.
- `dsf-control-center` — governance web process.

## Conventions

- Commit messages use Conventional Commits: `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`, `test:`, `ci:`, `perf:`, `build:`.
- Add tests with changes; prefer targeted xUnit filters first, then `dotnet test Dsf.sln --no-build` when practical.
- Read relevant ADRs before subsystem rewrites; current operator docs live under `docs/site/get-started/`.
- Current/living docs describe the .NET implementation. Treat older migration notes as historical only.

## Agent docs

- Issue tracker: `docs/agents/issue-tracker.md`.
- Triage labels: `docs/agents/triage-labels.md`.
- Domain docs: `docs/agents/domain.md`.
