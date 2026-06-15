# Infrastructure (`infra/`)

Infrastructure-as-code for the Dark Software Factory intake line. **These files
are authored for review and are NOT deployed automatically.** Provisioning real
Azure resources, and deploying the homelab agent, both require explicit human
action with credentials (design §13).

> **COST WARNING.** `azd up` / `az deployment group create` creates **billable**
> Azure resources: a Container Apps environment + 6 container apps, a Cosmos DB
> account (NoSQL + vector, autoscale to 1000 RU/s), App Configuration (Standard),
> Key Vault, Log Analytics, Application Insights, and an Event Grid topic.
> Expect ongoing charges (Cosmos + Log Analytics ingestion dominate). The
> Azure OpenAI / Foundry model resource is **referenced, not created** — supply
> an existing endpoint. Tear down with `azd down` / `az group delete` when done.

## What gets provisioned (`main.bicep`)

| Resource | Purpose |
|---|---|
| User-assigned managed identity | Single workload identity for all apps (RBAC subject) |
| Log Analytics workspace | Backing store for Container Apps logs + App Insights |
| Application Insights | OpenTelemetry GenAI traces / metrics (design §6.1) |
| Key Vault (RBAC, soft-delete + purge protection) | All secrets; identity gets **Key Vault Secrets User** |
| App Configuration (Standard) | Control Center backend; seeded feature flags |
| Cosmos DB (NoSQL + `EnableNoSQLVectorSearch`) | Unified memory; `dsf` db + TTL `memory` container w/ vector index |
| Container Apps managed environment | Wired to Log Analytics |
| 6 Container Apps | orchestrator, control-center, ingestion, agent-sentry, agent-foundryiq, agent-webiq |
| Event Grid topic | Signal ingestion (alert → webhook → Event Grid → ingestion endpoint) |

**Seeded feature flags** (App Configuration `keyValues`):
`dry_run` (enabled = global kill switch ON), `triggers_scheduled_paused`,
`triggers_signal_paused`, plus `dsf:default_confidence_threshold = 0.65`.

**Ingress:** `control-center` and `ingestion` are **external**; `orchestrator`
and the three agents are **internal** to the environment (A2A only). Every app
gets the managed identity and `DSF_MODE=azure` plus Cosmos / App Config /
Key Vault / OpenAI / App Insights / Event Grid endpoints as env vars.

**Referenced, not created:** the Azure OpenAI / Foundry endpoint + deployment
are `param openAiEndpoint` / `param openAiDeployment`. No model deployment is
provisioned by this template.

### Modules
- `modules/containerapp.bicep` — one reusable Container App (name / image /
  identity / ingress / env / scale). Tags `azd-service-name` so `azd deploy`
  binds builds to apps.
- `modules/cosmos.bicep` — Cosmos account + db + vector/TTL container + the
  data-plane SQL role assignment for the identity.

### Outputs
`controlCenterUrl`, `ingestionUrl`, `cosmosEndpoint`, `appConfigEndpoint`,
`keyVaultUri`, `eventGridTopicEndpoint`, `identityClientId`,
`appInsightsConnectionString`.

## Validate (no deploy)

```bash
az bicep build --file infra/main.bicep
az bicep build --file infra/modules/cosmos.bicep
az bicep build --file infra/modules/containerapp.bicep
```

All three compile clean (the lint-only build emits `*.json` artifacts you can
delete). This is the only step run during the authoring phase.

## Deploy — cloud (when a human is awake)

Prerequisites: `az` + `azd`, an Azure subscription, and an **existing** Azure
OpenAI / Foundry endpoint + deployment.

### Option 1 — azd (builds images + provisions + deploys)

```bash
az login
azd auth login
azd env new dsf-dev
azd env set OPENAIENDPOINT  "https://<your-aoai>.openai.azure.com/"
azd env set OPENAIDEPLOYMENT "gpt-4o"
azd up        # provisions main.bicep, then builds & deploys all 6 services
```

### Option 2 — bicep only (provision platform; deploy images separately)

```bash
az login
az group create -n rg-dsf-dev -l swedencentral
az deployment group create \
  -g rg-dsf-dev \
  -f infra/main.bicep \
  -p namePrefix=dsf environmentName=dev \
     openAiEndpoint="https://<your-aoai>.openai.azure.com/" \
     openAiDeployment="gpt-4o" \
     adminPrincipalId="<your-aad-object-id>"
```

Validate first with `--what-if` (no changes applied):

```bash
az deployment group what-if -g rg-dsf-dev -f infra/main.bicep -p namePrefix=dsf ...
```

Tear down: `azd down` or `az group delete -n rg-dsf-dev`.

## Deploy — homelab Grafana agent

The Grafana agent runs in the homelab (Proxmox) and reaches the cloud control
plane over an **outbound tunnel** — no inbound ports (design §8/§10).

```bash
cp infra/.env.homelab.example infra/.env.homelab   # then fill it in
docker compose -f infra/compose.homelab.yml --env-file infra/.env.homelab up -d
```

Configure the tunnel sidecar in `compose.homelab.yml` (Cloudflare Tunnel is
active by default; a Tailscale alternative is commented inline). Point the
cloud Product Registry's Grafana agent endpoint at the tunnel hostname. The
agent still enforces the `A2A_BEARER_TOKEN`, so the tunnel carries
authenticated traffic only.

## Notes / deviations
- `enablePurgeProtection: true` on Key Vault is intentional (security posture);
  it means the vault cannot be hard-deleted for 90 days after a soft delete.
- Cosmos `defaultTtl: -1` enables TTL with no blanket expiry; the working-memory
  tier sets per-item `ttl`.
- Container App default image is `mcr.microsoft.com/k8se/quickstart` so the
  template provisions before any app image exists; `azd` overwrites it on deploy.
