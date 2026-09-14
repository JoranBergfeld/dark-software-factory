# .NET workspace

This directory is the active Dark Software Factory implementation.

## Layout

- `src/Dsf.Core` — shared domain, contracts, runtime settings, product records, and charters.
- `src/Dsf.FeatureCouncil` — Feature Council conveyor and stations.
- `src/Dsf.Cli` — packaged `dsf` global tool; factory provisioning, charter commands, runtime forwarding.
- `src/Dsf.Runtime` — deployed runtime host for `run`, `sweep`, `serve-orchestrator`, `serve-agent`, and `poll-outcomes`.
- `src/Dsf.ControlCenter` — governance web process (`dsf-control-center`).
- `src/Dsf.AgentHost` — reusable source-agent host.
- `src/Dsf.Testing` — deterministic test helpers; production projects must not reference it.
- `tests/*` — xUnit suites, including module-boundary tests.

## Common commands

Source builds require the .NET 10 SDK and Bicep CLI on `PATH` (release builds pin
Bicep 0.44.1). Bicep is a **build-time** prerequisite, not an installed-CLI prerequisite.
To use a compiler outside `PATH`, pass `-p:BicepCommand=/absolute/path/to/bicep`.

```bash
dotnet restore Dsf.sln --locked-mode
dotnet build Dsf.sln --no-restore
dotnet test Dsf.sln --no-build
dotnet list Dsf.sln package --vulnerable --include-transitive
```

Target one suite or test:

```bash
dotnet test tests/Dsf.Runtime.Tests/Dsf.Runtime.Tests.csproj --no-build
dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter FullyQualifiedName~CliSurfaceTests --no-build
```

## Package and publish

Build the NuGet global tool:

```bash
dotnet pack src/Dsf.Cli/Dsf.Cli.csproj -c Release -o artifacts/release/nuget
```

Publish a self-contained CLI archive payload:

```bash
dotnet publish src/Dsf.Cli/Dsf.Cli.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=false -o artifacts/release/linux-x64
```

Supported release runtime identifiers are `linux-x64`, `linux-arm64`, `osx-x64`,
`osx-arm64`, `win-x64`, and `win-arm64`.

Keep the entire payload together: `dsf`, `runtime/`, and `assets/infra/`.
NuGet includes the framework-dependent runtime host and requires the .NET 10 SDK
(including the ASP.NET Core runtime). Native archives include self-contained CLI and runtime
executables and require neither the SDK nor a separately installed .NET runtime.

The CLI's build-only runtime dependency builds the matching runtime automatically,
including for `dotnet run --project src/Dsf.Cli -- sweep --product <product>`.
`eng/CliBundle.targets` publishes its dependency closure into `runtime/` and compiles
the five provisioning entrypoints into self-contained ARM JSON under `assets/infra/`.
Use one `-p:Version=<version>` for build, pack, and publish; when using `--no-build`,
the configuration and version must match the preceding build.

Offline installation checks (no Azure/GitHub writes):

```bash
./eng/smoke-test-tool-package.sh artifacts/release/nuget/DarkSoftwareFactory.Cli.<version>.nupkg
./eng/smoke-test-release-archive.sh <archive.tar.gz> linux-x64
```

## Runtime host

`src/Dsf.Runtime` (`dsf-runtime`) is the deployed runtime entrypoint. The `dsf` front door
automatically resolves the bundled host under `runtime/`, then forwards runtime verbs without an
assembly reference. `DSF_RUNTIME_HOST` remains an advanced override for a custom host executable.

- `run --signal <path> [--dry-run] [--product <product>]` — parse a signal and drive the
  Feature Council conveyor. Dry-run stops at filing and prints the issues it would file.
- `sweep [--dry-run] [--product <product>]` — read the enabled source-agent roster and drive
  a scheduled run.
- `serve-orchestrator [--host --port --loop --interval --product]` — serve health/run
  endpoints; with `--loop`, sweep continuously.
- `serve-agent --kind <kind> [--host --port]` — serve one source agent's A2A card and gather
  endpoint.
- `poll-outcomes [--product <product>]` — record audited learning data from downstream human
  outcome labels.

## Runtime dependency composition

Every conveyor-driving verb composes collaborators before work starts and fails naming each
unset setting rather than running with missing dependencies:

- **Source agents** — in-process unless a remote source-agent endpoint is configured.
- **Filing** — GitHub REST filer authenticated as the DSF GitHub App.
- **Persistence** — Cosmos run blackboard, written after every station checkpoint.
- **Model** — Azure OpenAI chat completions for synthesis and council reasoning.
- **Tracing** — Application Insights; trace failures are audited but do not decide run status.

## Control Center

`src/Dsf.ControlCenter` (`dsf-control-center`) is a separate governance web process. It refuses
to start without `DSF_OWNER_APPCONFIG_ENDPOINT` and `DSF_CONTROL_CENTER_TOKEN`. Optional
settings are `DSF_CONTROL_CENTER_HOST`, `DSF_CONTROL_CENTER_PORT`, and
`DSF_CONTROL_CENTER_SECURE_COOKIES`.

## Dependency management

- `global.json` pins the SDK (`10.0.301`, `rollForward: latestPatch`).
- `Directory.Packages.props` enables Central Package Management with exact direct versions.
- Each project restores with committed `packages.lock.json`; CI restores in locked mode.
- `NuGet.config` maps all packages to `nuget.org` only.
- `Directory.Build.props` enables NuGet audit for direct dependencies.
