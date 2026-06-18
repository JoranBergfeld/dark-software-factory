# Infrastructure (`infra/`)

Infrastructure-as-code for the Azure **backing services** the Dark Software Factory
relies on **and** the runtime that consumes them. The feature-council orchestrator
runs as an **Azure Container App** in the same resource group, authenticating to the
data services with a **user-assigned managed identity** (see ADR 0004). No inbound
exposure — it is a worker that reaches its sources over their authenticated endpoints.

**These files are authored for review and are NOT deployed automatically.**

> **COST WARNING.** Provisioning creates **billable** Azure resources: Cosmos DB
> (NoSQL + vector, autoscale to 1000 RU/s), App Configuration (Standard), Key
> Vault, Log Analytics, Application Insights, a Service Bus namespace (Standard),
> an Event Grid topic, and a **Container Apps environment + orchestrator app**.
> Cosmos + Log Analytics ingestion dominate ongoing cost. Azure OpenAI / Foundry is
> **referenced by the runtime, not created here.** Tear down with `az group delete`
> when done.

## What gets provisioned (`main.bicep`)

| Resource | Purpose |
|---|---|
| Log Analytics workspace | Backing store for Application Insights |
| Application Insights | OpenTelemetry GenAI traces / metrics (design §6.1) |
| Key Vault (RBAC, soft-delete + purge protection) | Secrets; the runtime identity gets **Key Vault Secrets User** |
| App Configuration (Standard) | Control Center backend; seeded feature flags |
| Cosmos DB (NoSQL + `EnableNoSQLVectorSearch`) | Unified memory; `dsf` db + TTL `memory` container w/ vector index |
| Service Bus (Standard) + `signals` queue | Ingestion buffer the orchestrator polls **outbound** |
| Event Grid custom topic | Where external signal sources publish; delivered into the queue via the topic's identity |
| User-assigned managed identity | The runtime's identity; holds all data-plane roles below |
| Container Apps environment + `dsf-orchestrator-<product>` app | The feature-council orchestrator worker (no ingress) |

**Runtime identity & roles.** A **user-assigned managed identity** is created and
attached to the orchestrator Container App; it holds the data-plane roles: Cosmos
Data Contributor, App Configuration Data Reader, Key Vault Secrets User, Service Bus
Data Receiver. (A user-assigned — not system-assigned — identity has a stable
principalId known before the app, avoiding a dependency cycle between the app's env
and the resources it references; see ADR 0004.) `DefaultAzureCredential` selects it
via the `AZURE_CLIENT_ID` env the Bicep wires in. The `product` and `runtimeImage`
params name the app (`dsf-orchestrator-<product>`) and choose its image.

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
`signalsQueueName` — these feed the runtime's `azure`-mode config — plus
`runtimePrincipalId` (the managed identity) and `orchestratorAppName`
(`dsf-orchestrator-<product>`, used for `az containerapp` image rolls).

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
Change-detection: an agent rebuilds only when its own `feature-council/src/dsf/agents/<kind>/**`
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

# 1) Preview, then provision the backing services, the runtime identity, and the
#    orchestrator Container App. For a throwaway dev deploy pass
#    enablePurgeProtection=false so a deleted Key Vault name can be reused (the
#    default true reserves the vault name for 90 days). Pass the product slug and
#    runtime image (the managed identity + its data-plane roles are created here).
az deployment group what-if -g rg-dsf -f infra/main.bicep -p @infra/main.parameters.json
az deployment group create  -g rg-dsf -f infra/main.bicep -p @infra/main.parameters.json \
  -p enablePurgeProtection=false -p product=<product> -p runtimeImage=<ghcr.io/...>

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

## Runtime (Azure Container Apps)

The orchestrator runs as the `dsf-orchestrator-<product>` Container App created by
`main.bicep`, with the user-assigned identity attached and `DSF_MODE=azure`. It reads
its endpoints from the template outputs (wired into the app's env) and consumes signal
interrupts by **polling the Service Bus `signals` queue outbound** — there is no
ingress. The agent images are published to GHCR by the `agents-images` pipeline.

### Per-product council runtime (rendered by `dsf new`)

`dsf new <product>` provisions a *dedicated* RG per product (including the runtime
identity + orchestrator Container App) and renders that product's runtime descriptor
to `config/instances/<product>.runtime/` — a generated `containerapp.yaml` for
`feature-council/src/dsf/runtime/Dockerfile` plus a resolved `.env.orchestrator` whose endpoints come
straight from that deployment's Bicep outputs (App Config, Key Vault URI, App
Insights, Cosmos). **Only endpoints are rendered; secrets stay in Key Vault** and are
fetched at runtime via the managed identity. Under `--execute` `dsf new` rolls the app
image with `az containerapp update`. See `docs/RUNBOOK.md` → *Creating a product
instance*.

## Notes
- `enablePurgeProtection: true` on Key Vault is intentional; the vault cannot be
  hard-deleted for 90 days after a soft delete.
- Cosmos `defaultTtl: -1` enables TTL with no blanket expiry; the working-memory
  tier sets per-item `ttl`.
- Event Grid → Service Bus delivery uses the topic's system-assigned identity
  (no SAS keys); the role assignment is created before the subscription.
