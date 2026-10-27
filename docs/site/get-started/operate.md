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

The global tool and every release archive include a matching runtime host under `runtime/`.
`dsf` automatically forwards `run`, `sweep`, `serve-orchestrator`, `serve-agent`, and
`poll-outcomes` to that host. No separate build/install or environment override is needed.
Source invocation also builds and resolves the matching host automatically.

The standalone `Dsf.AgentHost` executable uses the same process boundary: it
forwards `--kind <kind>` (or `serve-agent --kind <kind>`) to its bundled runtime,
without importing the Runtime module. Publishing `Dsf.AgentHost.csproj` includes
the matching `runtime/` directory; deploy that complete output, not the executable
alone. Its agent serves `/healthz`, `/.well-known/agent-card.json`, and `/gather`.

`DSF_RUNTIME_HOST=/absolute/path/to/dsf-runtime` remains an advanced override for a
custom executable. Unset it to use the bundled version. Missing/corrupt bundle errors
require reinstalling the tool or re-extracting the complete archive, not cloning this repository.

Manual operator checks use the `dsf` front door:

```bash
dsf run --product <product> --signal /absolute/path/to/operator-signal.json --dry-run
dsf sweep --product <product> --dry-run
dsf serve-agent --kind azuremonitor --host 127.0.0.1 --port 8082
```

These commands launch **locally**, not inside the deployed factory. Bundling does not grant
Azure authentication, bypass firewalls, or provide access to internal source-agent ingress.
The existing authorized network-path requirements below still apply.

### Sweep controls and live acceptance

Sweep controls are product App Configuration values, not Container App updates:

```bash
dsf sweep pause --product <product>
dsf sweep resume --product <product>
dsf sweep interval 300 --product <product>
dsf sweep status --product <product>
```

The packaged CLI forwards these controls to the bundled runtime's App Configuration
data-plane client. Run all writes (`pause`, `resume`, and `interval`) from an
authorized operator workstation, authenticated as an operator with **App
Configuration Data Owner** on the product store. The client uses
`DefaultAzureCredential`; ensure it selects that operator identity (for example,
the approved `az login`), not another configured credential. The deployed runtime
identity has only **App Configuration Data Reader**; do not broaden its permissions
or run these writes in its console.

Keep `DSF_OWNER_APPCONFIG_ENDPOINT` set to the bootstrap owner endpoint and use
`--product` to discover the product's runtime settings from the owner index.
The operator needs read access to that index; explicit runtime environment values,
including `AZURE_APPCONFIG_ENDPOINT`, override indexed values, so remove stale
exports from other products. The workstation must have authorized connectivity
to both App Configuration stores. For private endpoints, use the approved private
network path and DNS (for example, an authorized VPN-connected workstation);
do not bypass firewalls or enable public access to run controls.

The running loop rereads controls at least once per second while idle; pausing
does not cancel a sweep already in progress.
Until an interval is stored, `status` reports the runtime's `DSF_SWEEP_INTERVAL`
fallback (300 seconds when unset). A stored interval overrides that fallback.

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

#### Joint staging checklist (#183)

This is a deferred operator procedure, **not evidence that #183 is complete**.
Run it together only after all branch tickets are implemented and the staging
product, vendor/model spend, and real GitHub filing have been explicitly approved.
Offline `EndToEndAcceptanceGateTests` cover wiring with scripted boundaries;
they do not replace any step below.

1. **Prepare an isolated staging product.** Follow
   [Enable Decide sources and judgment](provision-a-factory.md#enable-decide-sources-and-judgment)
   using the agreed immutable branch image. Start with sources disabled.
   Prepare, but do not yet apply, `decide.json` with `webiq` enabled and a
   meaningful `webIqQuery`. Use creation maturity `medium` and operation maturity `low`;
   keep all five lenses enabled and configure the three distinct supported
   juror families/deployments. Operation maturity `low` prevents automatic
   Coding Agent assignment; separately check staging repository workflows will
   not start unwanted downstream work on `creation:ready`.
   Seed `webiq-api-key` in the **product** vault through the approved secret
   process; do not put it in commands or evidence. Verify model access, the
   GitHub App installation/private key, tracing, and private connectivity.
   The provisioning template confirms live filing for the deployed loop:
   enabling sources before pausing can therefore file immediately.

2. **Record the deployment, pause, then open its console.** On the authorized
   operator workstation, verify the identity, roles, owner-index discovery, and
   network access described above. Keep the bootstrap owner settings exported
   in every workstation terminal used below. Replace these values with the
   manifest's effective names:

   ```bash
   export PRODUCT='<staging-product>'
   export RESOURCE_GROUP="rg-dsf-$PRODUCT"
   export NAME_PREFIX='<effective-name-prefix>'
   export ORCHESTRATOR="$NAME_PREFIX-orchestrator"
   export REPOSITORY='<owner/staging-repository>'
   mkdir -p evidence/ticket-183
   git rev-parse HEAD > evidence/ticket-183/branch-sha.txt
   az containerapp show --resource-group "$RESOURCE_GROUP" --name "$ORCHESTRATOR" \
     --query '{revision:properties.latestReadyRevisionName,containers:properties.template.containers[].{name:name,image:image,command:command,args:args},scale:properties.template.scale}' \
     --output json > evidence/ticket-183/deployment.json
   dsf sweep status --product "$PRODUCT"
   dsf sweep pause --product "$PRODUCT"
   dsf sweep status --product "$PRODUCT"
   ```

   Confirm the ready revision uses the agreed image digest, `dotnet
   dsf-runtime.dll serve-orchestrator --loop`, and `minReplicas: 1`. Retain the
   image-to-commit mapping; a workstation SHA alone does not identify a deployed
   image. Save initial pause/cadence values and the workstation transcript in the
   approved evidence store. Confirm any in-flight sweep has finished before proceeding;
   pause affects only the loop, not manual sweeps. Do not start another
   `serve-orchestrator --loop` in the console.

   Apply the prepared source configuration from the workstation, using the same
   saved manifest/config root and owner settings:

   ```bash
   dsf new --product "$PRODUCT" --creation-maturity medium \
     --operation-maturity low --decide-config decide.json --no-charter
   ```

   After rollout, repeat the deployment capture and workstation
   `dsf sweep status --product "$PRODUCT"`; require paused state before the
   preview. The saved immutable runtime image and App Configuration pause must
   survive this update. Then open the container console:

   ```bash
   az containerapp exec --resource-group "$RESOURCE_GROUP" --name "$ORCHESTRATOR" \
     --container orchestrator --command /bin/sh
   ```

   The console inherits the deployed read-only App Configuration identity and
   runtime settings. Use it for manual sweeps and runtime evidence with private
   source connectivity, not control writes. Inside it, use the image's existing
   runtime executable (the packaged `dsf` tool need not be installed):

   ```bash
   cd /app
   dotnet dsf-runtime.dll sweep status
   ```

   Retain the console transcript in the approved evidence store.

3. **Preview through the real services while paused.**

   ```bash
   dotnet dsf-runtime.dll sweep --dry-run
   ```

   This is the deployed-runtime equivalent of `dsf sweep --product "$PRODUCT"
   --dry-run`, not a remote invocation from a laptop. It still calls WebIQ and
   models, acquires the Cosmos lease, and persists run/learning data; it does not
   file issues or post external trace events. Retain the printed run ID and
   audit. Require real WebIQ evidence, S3 clusters with complete references,
   all five S5 lenses, and three valid model-diverse juror responses. Empty,
   killed, escalated, or errored output is not filing readiness. Fix access or
   configuration errors before continuing; never substitute fixtures or force
   a verdict.

4. **Run the manual live gate once, still paused.**

   ```bash
   DSF_CONFIRM_LIVE_FILING=true dotnet dsf-runtime.dll sweep
   ```

   This executes the same sweep path as `dsf sweep --product "$PRODUCT"`.
   Retain its complete transcript separately from the preview, including
   `[dsf] run <id>`, `dry_run=false`, S1–S7 checkpoints, `verdict=Proceed`, and
   the filed issue URL. A zero exit code alone is insufficient: lease contention
   is a successful skip, and nonfiling outcomes can also exit successfully.
   Do not approve/resume an escalation manually to make this gate pass.

5. **Observe a separate autonomous attempt.** In another workstation terminal,
   start a timestamped console-log capture before resuming:

   ```bash
   az containerapp logs show --resource-group "$RESOURCE_GROUP" --name "$ORCHESTRATOR" \
     --container orchestrator --type console --tail 300 --follow \
     | tee evidence/ticket-183/autonomous-console.log
   ```

   In a separate authorized operator workstation terminal (not the container
   console), with the same `$PRODUCT` and owner settings:

   ```bash
   dsf sweep interval 300 --product "$PRODUCT"
   dsf sweep resume --product "$PRODUCT"
   dsf sweep status --product "$PRODUCT"
   ```

   Wait at least the effective interval plus service-call time, without issuing
   a manual sweep. Retain the loop's run ID, terminal summary, revision, and UTC
   observation window. Both manual and autonomous runs record
   `trigger:scheduled`; that audit field alone cannot distinguish them.
   Require an actual completed autonomous conveyor attempt, not `/healthz`,
   `status` reporting running, or repeated authorization/firewall errors.
   Deduplication may prevent a second issue; the manual run supplies filing
   proof. During active work, retain the product's `sweep-lease` document from
   Cosmos Data Explorer; an overlapping attempt must skip, never file twice.
   If testing contention, use a second `sweep --dry-run` console only while
   ownership is observed and retain its explicit lease-skip output. Never
   delete the lease to manufacture a successful attempt.

6. **Export the persisted proof.** In authorized Cosmos Data Explorer, open the
   product database's `runs` container, partition `/product = <staging-product>`.
   For each captured preview, manual, and autonomous run ID, query and save the
   complete JSON result to the approved restricted evidence store:

   ```sql
   SELECT * FROM c WHERE c.product = '<staging-product>' AND c.id = '<run-id>'
   ```

   Inspect `evidence`, `proposals[].evidenceReferences`, `clusterEvidence`,
   `councilReview` (evidence, rounds, recommendation, models, jury verdicts,
   outcome), `audit`, `checkpoints`, `filedIssues`, and `dryRun`. References must
   resolve to real WebIQ records; the same reviewed evidence must reach S7.
   A single enabled kind is sufficient: require the real cross-source-capable
   clusterer, not fabricated evidence from a second kind. On the workstation,
   substitute the URL printed by the manual sweep:

   ```bash
   gh issue view '<manual-run-issue-url>' --repo "$REPOSITORY" \
     --json url,title,body,labels,author,createdAt \
     > evidence/ticket-183/manual-issue.json
   ```

   Verify the App filed it with `creation:ready`, traceable evidence, and the
   matching intent; retain model deployment/family configuration and available
   provider request IDs. SDK/protocol-shaped fixtures are not genuine vendor
   recordings. Keep credentials and tokens out of exports; retain sensitive
   evidence under restricted access, not in git.

7. **Stop further work and reconcile.** From the authorized operator workstation,
   using the same operator Data Owner identity, `$PRODUCT`, and owner settings:

   ```bash
   dsf sweep pause --product "$PRODUCT"
   dsf sweep status --product "$PRODUCT"
   ```

   Confirm paused state, allow active work to drain using container logs/runtime
   evidence, and stop log capture with Ctrl+C. Leave staging paused unless
   continued running is explicitly agreed; record the final controls and saved
   initial cadence for restoration. Any later cadence restoration or resume
   must also use workstation `dsf sweep interval <seconds> --product "$PRODUCT"`
   or `dsf sweep resume --product "$PRODUCT"`, never the container identity.
   Preserve run documents, leases/learning indexes, and issue links.
   Record unmet criteria rather than weakening
   maturity, jury diversity, grounding, or network policy. #183 remains open
   until the real manual filing and separate autonomous proof are reviewed
   together.

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

- `dsf charter init --product <product>` — interview, then open a charter PR with auto-merge.
- `dsf charter sync --product <product>` — force a sync now.
- `dsf charter status --product <product>` — print stored charter status and drift.
- `dsf charter implement --product <product>` — propose the constitution with auto-merge, wait for its
  merge, then file the application build issue and attempt agent assignment.
- `dsf charter watch --product <product>` — watch the build PR and request review when ready.

`sync`, `status`, and `implement` discover Cosmos settings from the owner App Configuration
index using `DSF_OWNER_APPCONFIG_ENDPOINT` and `--product`. Explicit `AZURE_COSMOS_ENDPOINT`
and `DSF_COSMOS_DATABASE` exports override indexed values; otherwise the database defaults
to the product key when no database is indexed. Missing or unreadable configuration fails
with an error, not a local-storage fallback.

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
