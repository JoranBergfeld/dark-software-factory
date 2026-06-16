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
  custom topic (system-assigned identity) + identity-based event subscription that
  delivers topic events into the queue, plus the conditional receiver role.

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
az group create -n rg-dsf-dev -l swedencentral
# Preview first:
az deployment group what-if -g rg-dsf-dev -f infra/main.bicep -p @infra/main.parameters.json
# Then apply (set workloadPrincipalId to your homelab SP object id):
az deployment group create -g rg-dsf-dev -f infra/main.bicep \
  -p @infra/main.parameters.json -p workloadPrincipalId="<homelab-sp-object-id>"
```

`azd provision` works too (`azure.yaml` is infra-only — there is no `azd up`
service deployment, by design). Tear down: `az group delete -n rg-dsf-dev`.

## Homelab runtime (your choice of host)

The agents/orchestrator/control-center run as containers in your homelab. Pull the
agent images from GHCR (or build locally), set `DSF_MODE=azure`, authenticate with
the homelab **service principal** (it has the data-plane roles above), and point
config at the template outputs. `compose.homelab.yml` shows the Grafana agent with
an outbound tunnel sidecar; extend it for the other agents as you see fit. Signal
interrupts are consumed by **polling the Service Bus `signals` queue outbound** —
nothing is pushed into the homelab.

## Notes
- `enablePurgeProtection: true` on Key Vault is intentional; the vault cannot be
  hard-deleted for 90 days after a soft delete.
- Cosmos `defaultTtl: -1` enables TTL with no blanket expiry; the working-memory
  tier sets per-item `ttl`.
- Event Grid → Service Bus delivery uses the topic's system-assigned identity
  (no SAS keys); the role assignment is created before the subscription.
