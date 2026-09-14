# Operate the factory

`dsf new` provisions the factory runtime, not your application. Start application
work with [Implement the application](implement-application.md).

Once sources and credentials are configured, the council sweeps sources and files
grounded `creation:ready` issues. The Creation phase builds them; the Operation phase
watches production once deployed. Operators govern from outside the line.

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
dsf run --product <product> --signal /absolute/path/to/operator-signal.json --dry-run
dsf sweep --product <product> --dry-run
dsf serve-agent --kind azuremonitor --host 127.0.0.1 --port 8082
```

### Sweep controls and live acceptance

Sweep controls are product App Configuration values, not Container App updates:

```bash
dsf sweep pause --product <product>
dsf sweep resume --product <product>
dsf sweep interval 300 --product <product>
dsf sweep status --product <product>
```

Source agents have internal ingress. Run a manual acceptance sweep from inside
the same Container Apps environment (for example, an orchestrator console);
a laptop cannot reach internal A2A endpoints just by resolving the owner index.
Configure the runtime's GitHub App secret, vendor credentials, tracing, and three
distinct chat-model families before attempting live filing.

For the live gate in #183, retain the manual run ID, its S3 evidence references
and S5 review records, the S7 `creation:ready` issue URL, and logs identifying an
autonomous sweep. `Proceed` is required to file; `Escalate`, `Kill`, and `Error`
are auditable outcomes, not successful filing evidence. Scripted test fixtures
do not satisfy this gate. A ticking process with repeated Cosmos authorization
or firewall errors is not a working sweep.

Do not relax network policy merely to make a proof pass: the runtime needs an
authorized network path to its Cosmos, App Configuration, Key Vault, and model
endpoints. The deployment adds `runs` and `learning` containers partitioned by
`/product`, without changing the partition keys of existing containers.

### Sweep lease recovery

Manual and periodic sweeps share one product-wide Cosmos document,
`id = "sweep-lease"` in the `runs` container. It does not expire: advancing the
clock or changing cadence must not allow a second worker to file concurrently.
Normal completion and cancellation release it with owner and ETag checks.
Contention reports an explicit skip, not a fabricated successful run.

After a crash or failed cleanup, first stop/drain every worker for that product
and confirm no manual sweep remains active. Only then remove the orphaned
`sweep-lease` document in the product partition and restart the orchestrator.
Never delete a lease solely because it is old. When upgrading from the old
window-keyed lease implementation, drain old revisions before starting the new
one; the two locking protocols cannot coordinate with each other.

### Problem identity retention

The `learning` container also holds nonexpiring `problem-index-*` documents,
one per run scope in the product partition. Each stores opaque problem IDs and
their representative evidence profiles. Conditional writes prevent concurrent
runs from allocating different IDs for the same recognized problem.

Preserve these indexes with the learning records: deleting or expiring them
loses recurring-intent associations and can permit duplicate issues. Ambiguous
profile matches, write failures, and Cosmos document-size limits stop S3 before
filing; identities are never silently evicted. Reconcile legacy per-source
intent markers with existing issues before a live upgrade: they do not contain
enough information to infer per-problem mappings automatically.

## Product charter

The charter (`.dsf/charter.md` in the product repository) states what the product is for. The
charter is not synchronized by runtime sweeps. The current .NET runtime does not read or amend
it during sweeps; manage charter state through the operator commands below.

Operator commands:

- `dsf charter init --product <product>` — interview, then open a PR adding the charter.
- `dsf charter sync --product <product>` — force a sync now.
- `dsf charter status --product <product>` — print stored charter status and drift.
- `dsf charter implement --product <product>` — propose the constitution, wait for its
  merge, then file the application build issue and attempt agent assignment.
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

The council files issues with the `creation:ready` label. The GitHub Cloud Agent
picks them up, opens PRs, and branch protection applies the product's creation-maturity setting.
Downstream approvals, edits, and rejections become lessons that inform future council runs.

```text
council issue → GitHub Cloud Agent PR → branch-protection gate → human outcome → lesson → next sweep
```

## Operation phase

`dsf new` provisions the managed Azure SRE Agent as a subscription-scoped deployment, the
Operation phase's implementation today. It watches production telemetry, investigates
incidents, and files issues or PRs carrying `creation:ready` and `incident`. Council sources
then pull those incidents into later proposals. See
[Operation phase](../concept/sre-agent.md) and [Handoffs](../concept/handoffs.md) for the full
mechanism.

## Guardrails

- Dry-run before live filing: `dsf run ... --dry-run` and `dsf sweep ... --dry-run`.
- Grounding gates require filed claims to trace to evidence.
- Deduplication and cost caps protect against source floods.
- Runtime composition fails loudly when required Azure or GitHub settings are absent.
