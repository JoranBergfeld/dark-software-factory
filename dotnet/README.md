# .NET workspace

This directory is the active Dark Software Factory implementation.

## Layout

- `src/Dsf.Core` — shared domain, contracts, runtime settings, product records, and charters.
- `src/Dsf.FeatureCouncil` — Feature Council conveyor and stations.
- `src/Dsf.Cli` — packaged `dsf` global tool; factory provisioning, charter commands, runtime forwarding.
- `src/Dsf.Runtime` — deployed runtime host for `run`, `sweep`, `serve-orchestrator`, `serve-agent`, `poll-outcomes`, and isolated `serve-github-auth`, `serve-telemetry`, `serve-models`, `serve-source`, `serve-supplemental`.
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
the twelve provisioning entrypoints into self-contained ARM JSON under `assets/infra/`.
Use one `-p:Version=<version>` for build, pack, and publish; when using `--no-build`,
the configuration and version must match the preceding build.

Offline installation checks (no Azure/GitHub writes):

```bash
./eng/smoke-test-tool-package.sh artifacts/release/nuget/DarkSoftwareFactory.Cli.<version>.nupkg
./eng/smoke-test-release-archive.sh <archive.tar.gz> linux-x64
```

## Existing repository attachment review

`dsf onboard decide supplemental --input selection.json` previews explicit
FoundryIQ/WebIQ opt-in; `--approve <fingerprint>` prepares distinct source identities,
narrow grants and actual `serve-supplemental` hosts from `onboard-supplemental.json`.
`--verify-only` rechecks every selected source without creation retries. Azure-only
needs none of these resources, and supplemental evidence never replaces fresh
dedicated application telemetry. See
[optional evidence sources](../docs/site/get-started/enable-optional-evidence-sources.md)
for complete interactive/redirected workflows and source-administrator handoffs.

The installed CLI includes `dsf onboard decide review --repo OWNER/REPO`.
It uses effective GitHub CLI authentication plus the prepared owner App
Configuration and Key Vault to return read-only, typed repository/access/label/
automation facts. `--json` provides explicit-input output without prompts.
No checkout, factory credentials, provisioning assets, remote mutations, or new
runtime verb are required for this review. Confirmation is not completed onboarding
or authorization to publish. See the
[operator guide](../docs/site/get-started/review-repository-attachment.md).

`dsf onboard decide plan` adds interactive or complete `--input selection.json`
application-boundary planning, with `--json` output and current
`--reviewed-fingerprint` replay checks. It discovers Azure using the operator's
explicit tenant/subscription, separates application resources from dedicated
evidence backends, and reports exact categories and administrator handoffs.
It creates no reservation, resource, grant, tag, secret copy or repository addition;
symbolic factory identities require later binding checks. No extra runtime or
provisioning asset is needed for this read-only capability. See
[boundary planning](../docs/site/get-started/preview-application-boundary.md).

`dsf onboard decide reserve --input selection.json --reviewed-fingerprint <hash>`
adds conditional owner admission, not provisioning. `status` and `resume` require
explicit `--owner-appconfig-endpoint`, `--product`, `--tenant`, and `--subscription`.
They recover the same identity from bounded remote plan/claim records; local
reports are not authority. All three support `--json`; incomplete upload repair
may supply the original `--input` to `resume`. No new asset or runtime verb is
needed. See [reservation and recovery](../docs/site/get-started/reserve-factory-identity.md).

`dsf onboard decide provision` adds separately approved **inactive** foundation
provisioning, durable ARM receipts and staged product-manifest authority.
It uses the packaged `onboard-foundation.json`, never the greenfield template.
No workers, grants, App key copy, application automation or activation are
included. Onboarding runtime admission fails closed; after resource dispatch,
`status`/`resume` inspect without retry or rollback. See
[inactive foundation provisioning](../docs/site/get-started/provision-inactive-foundation.md).

`dsf onboard decide github` adds exact-risk-approved, version-pinned owner key
copy, two authentication-module-only grants, an isolated deployed authentication
host and nonsecret actual GitHub proof. Interactive prompts have equivalent
explicit/redirected commands. `--verify-only` refreshes proven deployed authority
without repeating any creation effect; ordinary status/resume remain inspection.
The current product manifest points to `FactoryGitHubAuthority` and its
`GitHubAuthorityProof`/`FoundationOperation` records at canonical product keys.
Only the approved code identity can receive an ephemeral contents-read token;
no inactive caller receives issue-write authority. See
[GitHub authority](../docs/site/get-started/verify-factory-github-authority.md).

`dsf onboard decide models` separately approves two factory model deployments,
three existing diverse jurors, exact inference grants and the isolated model host.
`--verify-only` then proves real synthesis, embedding-driven deduplication and every
juror using the bound hosted identity. Interactive and explicit/redirected workflows
share current governance and immutable reviewed references. Verified consumer
endpoints reuse the same asynchronous workloads; no conveyor admission, persistent
problem-index migration or council readiness is implied. See
[required model capabilities](../docs/site/get-started/prove-model-capabilities.md).

## Runtime host

`dsf onboard decide source --prepare` separately approves the code-only source
host, private source-progress store and exact grants. `source` advances bounded,
immutable repository acquisition; `--status-only --page N` reads coverage and
`--new-snapshot` explicitly selects changed source. No models or target execution.
See [source acquisition and consumer APIs](../docs/site/get-started/acquire-source-inventory.md).
`dsf onboard decide infer` advances bounded, revisioned charter inference and
original-source semantic checks through the deployed code/model hosts.
`--status-only --page N` exposes private coverage and review data. Drafts never
govern or publish; [operator guide](../docs/site/get-started/infer-product-charter.md).

`dsf onboard decide charter` previews exact charter-only content and complete
private evidence. `--publish --approve <revision>` uses effective operator `gh`
identity to create a human-reviewed PR; `--adopt --approve <revision>` adopts
its exact human-merged result. `--adopt --existing` reviews unchanged current
intent without an empty PR. Adoption never activates; uncertainty retains
original receipts without forced retry.
[Interactive and explicit guide](../docs/site/get-started/publish-adopt-charter.md).

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
- `serve-github-auth` — dedicated inactive authentication host behind native Entra
  ingress; requires pinned product governance and the selected managed identity,
  not the legacy conveyor/model configuration. Its `/verify` endpoint returns
  proof; `/tokens/contents` requires the approved code caller; `/tokens/issues`
  denies publication. The CLI forwards to the matching bundled implementation.
- `serve-models` — dedicated inactive model host behind native Entra ingress;
  `/proof` checks all required routes, while `/synthesis`, `/deduplicate` and
  `/jurors/{juror}` require verified capability references and exact approved
  callers. Uses native Azure v1 inference and explicit managed identity, not
  legacy model defaults or developer credentials.
- `serve-source` — dedicated inactive acquisition host; authenticated operator
  `/acquire`, `/status`, `/infer` and `/intent-status`, code-only `/materialize`.
  Uses restricted GitHub tokens and separate source/intent namespaces in the
  isolated App Configuration store, never a raw-source archive or governance writer.

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
