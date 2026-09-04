# Operate the factory

A provisioned factory runs itself. The council sweeps sources, files grounded
`creation:ready` issues, the Creation phase builds them, and the SRE Agent watches production
and feeds incidents back to the start. Operators govern from outside the line.

## Runtime

`dsf new` deploys the council runtime as an Azure Container App (`<namePrefix>-orchestrator`)
in the product resource group. The app authenticates with a user-assigned managed identity and
reads endpoints from App Configuration, Cosmos, Key Vault, App Insights, and Azure OpenAI.
Secrets stay in Key Vault.

DSF is pull-only. The orchestrator gets work by sweeping enabled source agents; there is no
inbound work queue. The deployed app runs a continuous sweep loop:

```bash
dsf serve-orchestrator --product <product> --loop --interval 300
```

### Packaged CLI and runtime host

The packaged global tool and release archive publish `dsf` (`Dsf.Cli`) only. Runtime verbs
(`run`, `sweep`, `serve-orchestrator`, and `serve-agent`) are forwarded to a separate
`dsf-runtime` executable; they do not run from a standalone packaged `dsf`.

Deploy or run the runtime host separately through a source or service deployment. For a local `dsf`
front door, point `DSF_RUNTIME_HOST` at an existing, separately deployed or built runtime-host
executable before using a runtime verb:

```bash
export DSF_RUNTIME_HOST=/absolute/path/to/dsf-runtime
```

With `DSF_RUNTIME_HOST` configured, manual operator checks use the `dsf` front door:

```bash
dsf run --product <product> --signal tests/fixtures/sample_signal.json --dry-run
dsf sweep --product <product> --dry-run
dsf serve-agent --kind sentry --host 127.0.0.1 --port 8082
```

## Product charter

The charter (`.dsf/charter.md` in the product repository) states what the product is for. The
charter is not synchronized by runtime sweeps. The current .NET runtime does not read or amend
it during sweeps; manage charter state through the operator commands below.

Operator commands:

- `dsf charter init --product <product>` — interview, then open a PR adding the charter.
- `dsf charter sync --product <product>` — force a sync now.
- `dsf charter status --product <product>` — print stored charter status and drift.
- `dsf charter implement --product <product>` — render the constitution and file the bootstrap
  `creation:ready` issue.
- `dsf charter watch --product <product>` — watch the build PR and request review when ready.

`dsf charter` reaches the product repository through the master DSF GitHub App. Keep
`DSF_OWNER_KEYVAULT_URI` and `DSF_OWNER_APPCONFIG_ENDPOINT` exported so the CLI can resolve
App credentials and product records.

## Control Center

`dsf-control-center` is the governance web process. It refuses to start without
`DSF_OWNER_APPCONFIG_ENDPOINT` and `DSF_CONTROL_CENTER_TOKEN`.

```bash
DSF_OWNER_APPCONFIG_ENDPOINT=https://dsf-owner-cfg.azconfig.io \
DSF_CONTROL_CENTER_TOKEN=<operator-token> \
dsf-control-center
```

Operators use it to view products, inspect source-agent enablement, adjust confidence
thresholds, and see unsupported controls rendered disabled with reasons. Browser writes use a
server-issued session cookie plus CSRF token; automation uses bearer-authenticated API routes.

## Watching it

The runtime's `ApplicationInsightsTracer` posts a custom event per conveyor boundary
(`run.start`, `station.start`, `station.complete`, `station.error`, `run.complete`) to the
Application Insights ingestion endpoint parsed from `APPLICATIONINSIGHTS_CONNECTION_STRING`.
Dry runs never post: events carrying a `dryRun` property stay local to the run's audit trail.
Build workbooks or alerts against these event names directly in Application Insights; no
dashboard JSON ships with the release artifacts.

## Closed loop

The council files issues with the `creation:ready` label. The GitHub Copilot Coding Agent
picks them up, opens PRs, and branch protection applies the product's creation-maturity dial.
Downstream approvals, edits, and rejections become lessons that inform future council runs.

```text
council issue → Coding Agent PR → branch-protection gate → human outcome → lesson → next sweep
```

## SRE Agent

`dsf new` provisions the managed Azure SRE Agent as a subscription-scoped deployment. It watches
production telemetry, investigates incidents, and files issues or PRs carrying `creation:ready`
and `incident`. Council sources then pull those incidents into later proposals.

## Guardrails

- Dry-run before live filing: `dsf run ... --dry-run` and `dsf sweep ... --dry-run`.
- Grounding gates require filed claims to trace to evidence.
- Deduplication and cost caps protect against source floods.
- Runtime composition fails loudly when required Azure or GitHub settings are absent.
