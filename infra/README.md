# Infrastructure (`infra/`)

Infrastructure-as-code for the Azure backing services and runtime that Dark Software Factory
uses. The Feature Council orchestrator runs as an Azure Container App in the product resource
group, authenticating to data services with a user-assigned managed identity. No inbound work
endpoint is exposed; DSF pulls from authenticated sources.

**These files are authored for review and are not deployed automatically.**

> **Cost warning.** Provisioning creates billable Azure resources: Cosmos DB, App Configuration,
> Key Vault, Log Analytics, Application Insights, Azure AI Foundry deployments, AKS for the
> Creation phase, a Container Apps environment, and the orchestrator app. Delete product
> resource groups when finished.

## What `main.bicep` provisions

| Resource | Purpose |
|---|---|
| Log Analytics workspace | Backing store for Application Insights |
| Application Insights | Runtime traces and metrics |
| Key Vault | Secrets; runtime identity gets Key Vault Secrets User |
| App Configuration | Runtime and Control Center configuration |
| Cosmos DB | Run blackboard and memory |
| Azure AI Foundry | Chat and embedding deployments the runtime calls |
| User-assigned managed identity | Stable runtime identity for data-plane roles |
| Container Apps environment + orchestrator app | Feature Council worker running `dsf-runtime serve-orchestrator --loop` |

`main.bicep` outputs Cosmos, App Configuration, Key Vault, App Insights, and Azure OpenAI
endpoints. `dsf new` records those endpoints in the product instance manifest and wires the
Container App environment. Secrets remain in Key Vault.

## Runtime identity and roles

The user-assigned managed identity receives Cosmos data contributor, App Configuration data
reader, Key Vault secrets user, and Cognitive Services OpenAI user roles. The Container App
selects it through `AZURE_CLIENT_ID`.

## Configuration seeding

`main.bicep` provisions App Configuration control-plane resources. `dsf new` seeds flattened
runtime keys after deployment using the deploying principal's data-plane rights, retrying for
RBAC propagation. This keeps mutable product policy outside the template.

## CI pipelines

### `infra-whatif`

`.github/workflows/infra-whatif.yml` runs on infrastructure changes.

- `lint` compiles Bicep without Azure authentication.
- `what-if` runs an Azure deployment preview via OIDC when the repo variables
  `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, and
  `AZURE_RESOURCE_GROUP` are configured.

### `agents-images`

`.github/workflows/agents-images.yml` builds source-agent images and the .NET `dsf-runtime`
image, then pushes them to GHCR on the default branch. Pull requests build for validation but
do not push.

## Validate locally without deploying

```bash
az bicep build --file infra/main.bicep
az deployment group what-if -g <rg> -f infra/main.bicep -p @infra/main.parameters.json
```

## Provision manually when needed

The normal path is `dsf new`. For direct infrastructure work:

```bash
az login
az group create -n rg-dsf -l swedencentral
az deployment group what-if -g rg-dsf -f infra/main.bicep -p @infra/main.parameters.json
az deployment group create -g rg-dsf -f infra/main.bicep -p @infra/main.parameters.json \
  -p enablePurgeProtection=false -p product=<product> -p runtimeImage=<ghcr.io/...>
```

`azd provision` can provision the infrastructure definition; application rollout remains owned
by DSF release and provisioning workflows.
