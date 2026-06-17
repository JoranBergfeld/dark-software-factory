# Infrastructure (`infra/`)

Infrastructure-as-code for the Azure **backing services** the Dark Software Factory
relies on. The agent + orchestrator **runtime is hosted by you** (e.g. a Proxmox
homelab) and reaches these services **outbound** — no VNet peering, no inbound to
the homelab. This template provisions **no container/compute** (see ADR 0002).

**These files are authored for review and are NOT deployed automatically.**

> **COST WARNING.** Provisioning creates **billable** Azure resources: Cosmos DB
> (NoSQL + vector, autoscale to 1000 RU/s), App Configuration (Standard), Key
> Vault, Log Analytics, Application Insights, a Service Bus namespace (Standard),
> and an Event Grid topic. Cosmos + Log Analytics ingestion dominate ongoing cost.
> Azure OpenAI / Foundry is **referenced by your homelab runtime, not created
> here.** Tear down with `az group delete` when done.

## What gets provisioned (`main.bicep`)

| Resource | Purpose |
|---|---|
| Log Analytics workspace | Backing store for Application Insights |
| Application Insights | OpenTelemetry GenAI traces / metrics (design §6.1) |
| Key Vault (RBAC, soft-delete + purge protection) | Secrets; homelab SP gets **Key Vault Secrets User** |
| App Configuration (Standard) | Control Center backend; seeded feature flags |
| Cosmos DB (NoSQL + `EnableNoSQLVectorSearch`) | Unified memory; `dsf` db + TTL `memory` container w/ vector index |
| Service Bus (Standard) + `signals` queue | Ingestion buffer the homelab orchestrator polls **outbound** |
| Event Grid custom topic | Where external signal sources publish; delivered into the queue via the topic's identity |

**No compute.** There are no Container Apps, no managed environment. Data-plane
access is granted to a **homelab workload service principal** you supply via
`workloadPrincipalId` (its Entra object id): Cosmos Data Contributor, App Config
Data Reader, Key Vault Secrets User, Service Bus Data Receiver. Leave it empty to
provision the resources without role assignments and add them later.

**Seeded feature flags** (App Configuration): `dry_run` (enabled = global kill
switch ON), `triggers_scheduled_paused`, `triggers_signal_paused`, plus
`dsf:default_confidence_threshold = 0.65`.

### Modules
- `modules/cosmos.bicep` — Cosmos account + db + vector/TTL container + the
  conditional data-plane SQL role assignment.
- `modules/ingestion.bicep` — Service Bus namespace + `signals` queue + Event Grid
  custom topic (system-assigned identity) + the topic's Service Bus Data Sender
  role + the conditional receiver role.
- `ingestion-subscription.bicep` — **phase 2**, applied *after* `main.bicep`: the
  Event Grid → queue event subscription (identity-based delivery). Split out because
  Event Grid validates managed-identity delivery synchronously at creation, racing
  RBAC propagation of the sender role (aka.ms/egmsivalidation). Run it once the main
  deploy finishes and the role has propagated.

### Outputs
`cosmosEndpoint`, `appConfigEndpoint`, `keyVaultUri`,
`appInsightsConnectionString`, `eventGridTopicEndpoint`, `serviceBusNamespace`,
`signalsQueueName` — these feed your homelab runtime's `azure`-mode config.

## CI pipelines

### `infra-whatif` (`.github/workflows/infra-whatif.yml`)
Runs on every change under `infra/`.
- **`lint`** always runs: `az bicep build` (no Azure auth) — instant signal.
- **`what-if`** runs a real `az deployment group what-if` preview via **OIDC**,
  activating automatically once these **repo variables** are set
  (Settings → Secrets and variables → Actions → Variables):
  `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
  `AZURE_RESOURCE_GROUP`. Until then the job is skipped (lint still gates).

  One-time OIDC setup: create an Entra app registration, add a **federated
  credential** for this repo (e.g. subject `repo:<owner>/<repo>:ref:refs/heads/main`
  and one for `pull_request`), and grant it at least **Reader** on the target
  resource group (what-if needs read; deployment needs Contributor).

### `agents-images` (`.github/workflows/agents-images.yml`)
Builds each source agent as its own image and pushes to **GHCR**
(`ghcr.io/<owner>/dsf-agent-<kind>`), tagged `sha-<long>` and `latest` (on main).
Change-detection: an agent rebuilds only when its own `src/dsf/agents/<kind>/**`
changes; a change to shared core (`contracts`, `ports`, `a2a`, `agents/base.py`,
`container.py`, `pyproject.toml`) rebuilds **all** agents. Pull requests build for
validation but do **not** push. Uses the built-in `GITHUB_TOKEN` (no secrets).
**Deployment of these images is intentionally out of scope — host them however
you like.**

## Validate locally (no deploy)

```bash
az bicep build --file infra/main.bicep              # compiles main + both modules
az deployment group what-if -g <rg> -f infra/main.bicep -p @infra/main.parameters.json
```

## Provision (when a human is awake)

```bash
az login
# Pick a region with Cosmos capacity (West Europe is frequently constrained for
# zone-redundant Cosmos; Sweden Central worked at time of writing):
az group create -n rg-dsf -l swedencentral

# 1) Preview, then provision the backing services. For a throwaway dev deploy pass
#    enablePurgeProtection=false so a deleted Key Vault name can be reused (the
#    default true reserves the vault name for 90 days). Set workloadPrincipalId to
#    your homelab SP object id to also create the data-plane role assignments.
az deployment group what-if -g rg-dsf -f infra/main.bicep -p @infra/main.parameters.json
az deployment group create  -g rg-dsf -f infra/main.bicep -p @infra/main.parameters.json \
  -p enablePurgeProtection=false -p workloadPrincipalId="<homelab-sp-object-id>"

# 2) Phase 2 — wire Event Grid -> the Service Bus queue (after step 1 completes;
#    use the resource name suffix from step 1's outputs):
az deployment group create -g rg-dsf -f infra/ingestion-subscription.bicep \
  -p topicName=<dsf-egt-...> namespaceName=<dsf-sb-...>
```

> The `az eventgrid` CLI extension is currently broken on Windows
> (`_validate_subscription_id_matches_default_subscription_id` → `NoneType`), so
> create/list the subscription via the **ARM template above**, not `az eventgrid`.

`azd provision` works too (`azure.yaml` is infra-only — there is no `azd up`
service deployment, by design). Tear down: `az group delete -n rg-dsf`.

## Homelab runtime (your choice of host)

The agents/orchestrator/control-center run as containers in your homelab. Pull the
agent images from GHCR (or build locally), set `DSF_MODE=azure`, authenticate with
the homelab **service principal** (it has the data-plane roles above), and point
config at the template outputs. `compose.homelab.yml` shows the Grafana agent with
an outbound tunnel sidecar; extend it for the other agents as you see fit. Signal
interrupts are consumed by **polling the Service Bus `signals` queue outbound** —
nothing is pushed into the homelab.

### Per-product council runtime (rendered by `dsf new`)

`dsf new <product>` provisions a *dedicated* RG per product and then renders that
product's council runtime to `config/instances/<product>.runtime/` — a generated
`compose.orchestrator.yml` running `src/dsf/runtime/Dockerfile` plus an
`.env.orchestrator` whose endpoints come straight from that deployment's Bicep
outputs (App Config, Key Vault URI, App Insights, Cosmos). **Only endpoints are
rendered; secrets stay in Key Vault** and are fetched at runtime via the homelab
service principal. Under `--execute` (homelab target) `dsf new` brings the bundle up
with `docker compose ... up -d`. See `docs/RUNBOOK.md` → *Creating a product instance*.

## Notes
- `enablePurgeProtection: true` on Key Vault is intentional; the vault cannot be
  hard-deleted for 90 days after a soft delete.
- Cosmos `defaultTtl: -1` enables TTL with no blanket expiry; the working-memory
  tier sets per-item `ttl`.
- Event Grid → Service Bus delivery uses the topic's system-assigned identity
  (no SAS keys); the role assignment is created before the subscription.
