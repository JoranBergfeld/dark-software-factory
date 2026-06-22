# Infrastructure (`infra/`)

Infrastructure-as-code for the Azure **backing services** the Dark Software Factory
relies on **and** the runtime that consumes them. The feature-council orchestrator
runs as an **Azure Container App** in the same resource group, authenticating to the
data services with a **user-assigned managed identity** (see ADR 0004). No inbound
exposure — it is a worker that reaches its sources over their authenticated endpoints.

**These files are authored for review and are NOT deployed automatically.**

> **COST WARNING.** Provisioning creates **billable** Azure resources: Cosmos DB
> (NoSQL + vector, autoscale to 1000 RU/s), App Configuration (Standard), Key
> Vault, Log Analytics, Application Insights, an **Azure AI Foundry** account with
> chat + embedding model deployments, an **AKS cluster** (coding squad), and a
> **Container Apps environment + orchestrator app**. Cosmos + AKS + Log Analytics
> ingestion and model token usage dominate ongoing cost. Tear down with
> `az group delete` when done.

## What gets provisioned (`main.bicep`)

| Resource | Purpose |
|---|---|
| Log Analytics workspace | Backing store for Application Insights |
| Application Insights | OpenTelemetry GenAI traces / metrics (design §6.1) |
| Key Vault (RBAC, soft-delete + purge protection) | Secrets; the runtime identity gets **Key Vault Secrets User** |
| App Configuration (Standard) | Control Center backend; config seeded post-deploy by the `dsf` provisioner |
| Cosmos DB (NoSQL + `EnableNoSQLVectorSearch`) | Unified memory; `dsf` db + TTL `memory` container w/ vector index |
| Azure AI Foundry (`AIServices`) + chat & embedding deployments | The models the runtime calls; runtime identity gets **Cognitive Services OpenAI User** |
| User-assigned managed identity | The runtime's identity; holds all data-plane roles below |
| Container Apps environment + `dsf-orchestrator-<product>` app | The feature-council orchestrator worker (no ingress) |

**Runtime identity & roles.** A **user-assigned managed identity** is created and
attached to the orchestrator Container App; it holds the data-plane roles: Cosmos
Data Contributor, App Configuration Data Reader, Key Vault Secrets User, and
Cognitive Services OpenAI User (to call the Foundry models). (A
user-assigned — not system-assigned — identity has a stable
principalId known before the app, avoiding a dependency cycle between the app's env
and the resources it references; see ADR 0004.) `DefaultAzureCredential` selects it
via the `AZURE_CLIENT_ID` env the Bicep wires in. The `product` and `runtimeImage`
params name the app (`dsf-orchestrator-<product>`) and choose its image.

**Azure AI Foundry (created).** The runtime's `build_services()` **requires**
`AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_DEPLOYMENT`, and
`AZURE_OPENAI_EMBEDDING_DEPLOYMENT`. `main.bicep` creates an **Azure AI Foundry**
(`Microsoft.CognitiveServices/accounts`, `kind: AIServices`) account plus two model
deployments — chat (`chatModel`, default `gpt-4o`) and embedding (`embeddingModel`,
default `text-embedding-3-large`) — and grants the runtime identity **Cognitive
Services OpenAI User**. The model names/versions/capacities and the deployment SKU
(default `GlobalStandard`) are tunable params. The runtime calls them over the Azure
OpenAI data plane with its managed identity (AAD token, no keys); the container env
and outputs point at the created account, so `dsf new` needs no OpenAI flags.

**Runtime is a continuous worker.** The image runs `dsfctl serve-orchestrator
--loop`: it sweeps the enabled sources every `DSF_SWEEP_INTERVAL` seconds (default
300), surviving per-tick errors, so the always-on Container App revision stays
healthy (DSF is pull-only — there is no inbound ingress).

**Config seeding.** `main.bicep` provisions App Configuration **control-plane only**:
local auth is disabled (AAD-only) and the deploying principal is granted **App
Configuration Data Owner**. The config itself — the flattened `config/defaults.json`
(critic/agent enablement, thresholds, jury roster, the `triggers.*.paused` switches)
— is seeded **post-deploy** by the `dsf` provisioner (`seed_appconfig` step) via
`az appconfig kv set --auth-mode login`, retried to absorb the role-assignment RBAC
propagation lag. Keeping the writes out of the deployment avoids the in-template race
where ARM-proxied data-plane writes ran before the Data Owner grant propagated. An
empty store would read every `critic.*`/`agent.*` flag as disabled, so seeding the
baseline is required for the runtime to function.

### Modules
- `modules/cosmos.bicep` — Cosmos account + db + vector/TTL container + the
  conditional data-plane SQL role assignment.
- `modules/aks.bicep` — per-product AKS cluster running the coding-squad Ralph watch
  loop (ADR 0012).

The Azure SRE Agent (Phase 3) is a separate subscription-scoped deployment in
`sre-agent.bicep` (+ `modules/sre-*.bicep`), not part of `main.bicep`. See ADR 0015.

### Outputs
`cosmosEndpoint`, `appConfigEndpoint`, `keyVaultUri`, `appInsightsConnectionString`,
`appInsightsId`, `logAnalyticsId` — these feed the runtime config and the SRE agent
connectors — plus `runtimePrincipalId` (the managed identity), `orchestratorAppName`
(`dsf-orchestrator-<product>`, used for `az containerapp` image rolls), and the
`openaiEndpoint` / `openaiDeployment` / `openaiEmbeddingDeployment` of the created
Azure AI Foundry account + deployments (so the rendered `.env.orchestrator` record
matches the deployed container env).

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

# 1) Preview, then provision the backing services, the runtime identity, the Azure
#    AI Foundry account + model deployments, and the orchestrator Container App. For
#    a throwaway dev deploy pass enablePurgeProtection=false so a deleted Key Vault
#    name can be reused (the default true reserves the vault name for 90 days). Pass
#    the product slug and the runtime image; the Azure AI Foundry account + chat and
#    embedding deployments (and the managed identity + its data-plane roles) are
#    created here, so no OpenAI endpoint params are needed.
az deployment group what-if -g rg-dsf -f infra/main.bicep -p @infra/main.parameters.json
az deployment group create  -g rg-dsf -f infra/main.bicep -p @infra/main.parameters.json \
  -p enablePurgeProtection=false -p product=<product> -p runtimeImage=<ghcr.io/...>
```

`azd provision` works too (`azure.yaml` is infra-only — there is no `azd up`
service deployment, by design). Tear down: `az group delete -n rg-dsf`.

## Runtime (Azure Container Apps)

The orchestrator runs as the `dsf-orchestrator-<product>` Container App created by
`main.bicep`, with the user-assigned identity attached. It reads its endpoints from
the template outputs (wired into the app's env) and gets work by **sweeping the source
agents on a schedule** (pull-only, ADR 0014) — there is no ingress. The agent images
are published to GHCR by the `agents-images` pipeline.

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

Step 5 (`provision_azure`) deploys with `--no-wait` and streams per-resource progress to the
console while it polls; set `DSF_DEPLOY_POLL_INTERVAL` (seconds, default 5) to tune the cadence.

## Notes
- `enablePurgeProtection: true` on Key Vault is intentional; the vault cannot be
  hard-deleted for 90 days after a soft delete.
- Cosmos `defaultTtl: -1` enables TTL with no blanket expiry; the working-memory
  tier sets per-item `ttl`.
