---
title: "Azure Hosting Options for the Application-Driven Sweep Loop"
date: 2026-09-06
issue: 172
status: draft
---

# Azure Hosting Options for the Application-Driven Sweep Loop

## 1. Scope and Source Notes

This document researches and compares Azure hosting options for the `PeriodicSweepService : BackgroundService` (.NET 9) that will run under the `serve-orchestrator` CLI verb. All pricing was fetched from the Azure Retail Prices REST API on 2026-09-06. All documentation links are primary Microsoft Learn sources verified on the same date.

**Primary sources consulted:**

| Purpose | URL |
|---|---|
| ACA Consumption pricing, Sweden Central | `https://prices.azure.com/api/retail/prices?$filter=serviceName eq 'Azure Container Apps' and armRegionName eq 'swedencentral'` |
| ACI pricing, Sweden Central | `https://prices.azure.com/api/retail/prices?$filter=serviceName eq 'Container Instances' and armRegionName eq 'swedencentral'` |
| App Service B1 Linux pricing, Sweden Central | `https://prices.azure.com/api/retail/prices?$filter=serviceName eq 'Azure App Service' and armRegionName eq 'swedencentral' and contains(skuName, 'B1')` |
| ACI managed identity docs | `https://learn.microsoft.com/en-us/azure/container-instances/container-instances-managed-identity` |
| ACI restart policy docs | `https://learn.microsoft.com/en-us/azure/container-instances/container-instances-restart-policy` |
| ACA managed identity docs | `https://learn.microsoft.com/en-us/azure/container-apps/managed-identity` |
| ACA container/restart docs | `https://learn.microsoft.com/en-us/azure/container-apps/containers` |
| ACA revision/lifecycle docs | `https://learn.microsoft.com/en-us/azure/container-apps/revisions` |
| ACA health probes docs | `https://learn.microsoft.com/en-us/azure/container-apps/health-probes` |
| App Service managed identity docs | `https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity` |
| App Service container config docs | `https://learn.microsoft.com/en-us/azure/app-service/configure-custom-container` |
| ACA pricing page (free grant) | `https://azure.microsoft.com/en-us/pricing/details/container-apps/` |

**Repo context (verified from `infra/main.bicep`):** The `orchestratorApp` resource (`Microsoft.App/containerApps@2025-01-01`) is already fully provisioned with user-assigned managed identity (`UserAssigned`), `AZURE_CLIENT_ID` env var, 0.5 vCPU / 1 GiB, `minReplicas: 1`, `maxReplicas: 1`, `activeRevisionsMode: 'Single'`, and Log Analytics integration. The only missing piece is `command`/`args` to run `serve-orchestrator`.

---

## 2. Option 1: Azure Container Apps — Always-On, Consumption Plan (Current Shape)

### Overview

The simplest path: the bicep already provisions `orchestratorApp` in the Consumption plan with `minReplicas: 1`. The workload requires no inbound surface (no ingress), which ACA supports natively. The only bicep change needed is adding a `command`/`args` entry to the container template to invoke `serve-orchestrator` instead of the default entrypoint.

### Managed Identity

**Verified.** ACA supports user-assigned managed identities. From the official docs: *"A user-assigned identity is a standalone Azure resource that you can assign to your container app … User-assigned identities exist until you delete them."* ([Managed identities in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity)). The repo already wires this correctly:

```bicep
identity: {
  type: 'UserAssigned'
  userAssignedIdentities: {
    '${runtimeIdentity.id}': {}
  }
}
// env: AZURE_CLIENT_ID = runtimeIdentity.properties.clientId
```

`DefaultAzureCredential` in the container will pick up `AZURE_CLIENT_ID` and use the user-assigned identity automatically. No changes needed. ([`infra/main.bicep`, `orchestratorApp` resource])

### Restart Behavior

ACA provides two distinct restart mechanisms:

1. **In-process crash restart:** *"If a container crashes, it automatically restarts."* ([Containers in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/containers)). This is a container-level restart handled by the underlying managed infrastructure. Restart latency is not documented with a specific SLA, but image pull is avoided after the first pull since the image is cached.

2. **Revision-scope restart (redeploy):** A new revision is created when the container template changes (e.g., new image tag, new env var). In single revision mode (`activeRevisionsMode: 'Single'`), the revision lifecycle progresses: **Provisioning → Provisioned → Running**, and the old revision is deprovisioned once the new replica reaches `Running` state. ([Update and deploy changes in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/revisions)). This gives near-zero-downtime deploys without deployment slots.

3. **Liveness/readiness probes:** ACA supports HTTP and TCP liveness, readiness, and startup probes ([Health probes in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/health-probes)). Since `serve-orchestrator` exposes no HTTP endpoint, the application can be left without a liveness probe and rely on process-exit detection, or a minimal internal HTTP health endpoint can be added.

**Implication for the Cosmos lease:** The Cosmos-backed ETag lease guards against double-triggering on fast restart. On a crash-restart, the container re-initialises and attempts to acquire the lease. If the lease TTL is shorter than the typical restart latency (seconds), the lease will expire before the new process starts, and the restarted process acquires it cleanly. The ETag conditional write correctly handles concurrent acquisition if lease TTL overlaps restart.

### Observability

Log Analytics is already wired in the repo (`containerEnv` → `appLogsConfiguration: destination: 'log-analytics'`). All `stdout`/`stderr` from the container is shipped to the workspace. Application Insights is also provisioned and `APPLICATIONINSIGHTS_CONNECTION_STRING` is injected as an env var, enabling structured telemetry, distributed tracing, and metrics. No additional configuration is needed. ([`infra/main.bicep`, containerEnv and orchestratorApp resources])

### Cost: 30-Day Calculation (Sweden Central, Verified 2026-09-06)

**Verified rates from the Azure Retail Prices API** (`effectiveStartDate: 2022-06-01`, confirmed unchanged from prior research):

| Meter | Rate | Unit |
|---|---|---|
| Standard vCPU Active | **$0.000024** | per vCPU-second |
| Standard vCPU Idle | **$0.000003** | per vCPU-second |
| Standard Memory Active | **$0.000003** | per GiB-second |
| Standard Memory Idle | **$0.000003** | per GiB-second |

> **Active threshold:** A replica is active when vCPU usage exceeds 0.01 cores or data received exceeds 1,000 bytes/second. ([Azure Container Apps pricing page](https://azure.microsoft.com/en-us/pricing/details/container-apps/))

For a timer loop that sleeps 5–60 minutes between short HTTP sweeps, nearly all time is spent below the 0.01 vCPU threshold → **billed predominantly at the idle rate**.

**Constants:**
- Seconds in 30 days = 30 × 86,400 = **2,592,000 s**
- Free grant: 180,000 vCPU-seconds / 360,000 GiB-seconds per subscription per month

**Free grant analysis:**
- 0.5 vCPU × 2,592,000 s = **1,296,000 vCPU-seconds consumed** → free grant covers 180,000 (~13.9%)
- 1 GiB × 2,592,000 s = **2,592,000 GiB-seconds consumed** → free grant covers 360,000 (~13.9%)
- **The free grant is not material for an always-on replica.** It offsets approximately 2 days of consumption.

**Scenario A — 100% idle (conservative / worst-case for billing tier):**

| Component | Calculation | Cost |
|---|---|---|
| vCPU idle (gross) | 0.5 × 2,592,000 × $0.000003 | $3.89 |
| Memory idle (gross) | 1.0 × 2,592,000 × $0.000003 | $7.78 |
| **Gross total** | | **$11.67** |
| vCPU free grant savings | 180,000 × $0.000003 | −$0.54 |
| Memory free grant savings | 360,000 × $0.000003 | −$1.08 |
| **Net total after free grant** | | **≈ $10.05/month** |

**Scenario B — 5% active (realistic: ~72 min/day of active sweep time):**

| Component | Calculation | Cost |
|---|---|---|
| vCPU active (5%) | 0.5 × 129,600 × $0.000024 | $1.56 |
| vCPU idle (95%) | 0.5 × 2,462,400 × $0.000003 | $3.69 |
| Memory (same rate both states) | 1.0 × 2,592,000 × $0.000003 | $7.78 |
| **Gross total** | | **$13.03** |
| Free grant savings (est.) | ~$1.62 | −$1.62 |
| **Net total after free grant** | | **≈ $11.41/month** |

**Summary: ~$10–$13/month depending on active fraction.**

### Migration Cost

**Zero.** The resource is already provisioned and correctly configured. The only change required is adding a `command`/`args` block in the container template inside `infra/main.bicep`.

---

## 3. Option 2: Azure Container Instances (ACI)

### Overview

ACI is a fully managed container group service — no orchestration layer, just a container group that runs until stopped. For an always-on workload with `restartPolicy: Always` (the default), the container group keeps the container alive indefinitely.

### Managed Identity

**Verified.** From the official docs: *"Container Instances supports both types of managed Azure identities: user-assigned and system-assigned. On a container group, you can enable a system-assigned identity, one or more user-assigned identities, or both types of identities."* ([Enable Managed Identity in a Container Group - Azure Container Instances](https://learn.microsoft.com/en-us/azure/container-instances/container-instances-managed-identity)).

User-assigned MI is fully supported. The `AZURE_CLIENT_ID` env var pattern works the same way as ACA. **However**, the same docs note that modifying the identity on a running container group triggers a restart of the container group.

### Restart Behavior

ACI has a per-container-group `restartPolicy` property with three values ([Restart policy for run-once tasks - Azure Container Instances](https://learn.microsoft.com/en-us/azure/container-instances/container-instances-restart-policy)):

| Policy | Behaviour |
|---|---|
| `Always` (default) | Always restarted when the container process exits. **Correct for always-on.** |
| `OnFailure` | Restarted only on non-zero exit code |
| `Never` | Never restarted |

**Critical operational limitation:** ACI has **no equivalent of ACA's rolling revision deployment**. Updating the container group (e.g., new image) requires stopping and recreating the group, resulting in a hard cold-restart with a gap in uptime. The Cosmos ETag lease handles this gracefully, but the operational experience is worse than ACA's revision-based rolling update.

ACI offers **no node-level health probes** (no liveness/readiness probe mechanism comparable to Kubernetes). Restart is purely process-exit driven.

### Observability

ACI does not have built-in Log Analytics integration comparable to ACA's environment-level forwarding. Log collection requires a separate sidecar container or Azure Monitor Diagnostic Settings, neither of which is as seamless as ACA's native `appLogsConfiguration`. Application Insights SDK integration works (the SDK emits to HTTPS endpoints directly), but there is no environment-level Log Analytics forwarding. Migration would require new observability infrastructure.

### Cost: 30-Day Calculation (Sweden Central, Verified 2026-09-06)

> **Note on API query:** The Azure Retail Prices API service name for ACI is **`Container Instances`** (not `Azure Container Instances`).

**Verified rates** (`effectiveStartDate: 2021-06-08`):

| Meter | Rate | Unit |
|---|---|---|
| Standard vCPU Duration | **$0.0466** | per vCPU-hour |
| Standard Memory Duration | **$0.00511** | per GB-hour |

ACI bills at **one rate continuously** — there is no idle/active distinction. No free grant applies to ACI.

**30-day calculation (0.5 vCPU / 1 GiB, 720 hours):**

| Component | Calculation | Cost |
|---|---|---|
| vCPU | 0.5 × 720 × $0.0466 | $16.78 |
| Memory | 1.0 × 720 × $0.00511 | $3.68 |
| **Total** | | **$20.46/month** |

ACI is approximately **75% more expensive** than ACA for this always-on workload.

### Migration Cost

**High.** Requires:
- New `Microsoft.ContainerInstance/containerGroups` Bicep resource (different shape from `Microsoft.App/containerApps`)
- Removal or bypass of the existing `Microsoft.App/containerApps` resource
- New deployment tooling (`az container create`/`az container update` instead of `az containerapp update`)
- Manual wiring of observability (Log Analytics sidecar or Diagnostic Settings)
- Loss of ACA's rolling-revision deployment model

---

## 4. Option 3: Azure App Service (Web App for Containers, Always-On)

### Overview

App Service for Linux supports custom container images on a Basic (B) or higher App Service Plan. The "Always On" setting (available from B tier upward) prevents the platform from idling the app after inactivity. The workload runs as the container's entry process; no inbound HTTP is required, though App Service always allocates an HTTP listener (it can be left unused).

### Managed Identity

**Verified.** From the official docs: *"A user-assigned identity is a standalone Azure resource that can be assigned to your app. An app can have multiple user-assigned identities."* ([Use managed identities in Azure App Service](https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity)). Setting `AZURE_CLIENT_ID` to the user-assigned identity's client ID enables `DefaultAzureCredential` to resolve it automatically — identical to the ACA pattern.

### Restart Behavior

- **"Always On"** prevents the App Service platform from recycling the worker process due to inactivity. This setting is required on B1 and higher tiers and must be explicitly enabled.
- **Crash restart:** App Service auto-restarts the worker process on crash, consistent with its standard Linux container hosting model.
- **Deployment restart:** On B tier there are no deployment slots (Standard tier and above required), so deploying a new container image causes a brief cold restart of the single instance. This is worse than ACA's revision-based rolling update for the same tier.
- **Health checks:** App Service supports HTTP health check pings; for a container with no HTTP listener, health checks would need to be disabled or a minimal endpoint added.

### Observability

App Service has built-in Application Insights integration and log streaming via the Kudu console. However, the repo already has a Log Analytics workspace and Application Insights provisioned and wired for the ACA environment. Migrating to App Service would require re-wiring the Application Insights connection string into a different resource type and losing the ACA-integrated Log Analytics environment-level stream. The net observability capability is comparable, but the integration path is different.

### Cost: 30-Day Calculation (Sweden Central, Verified 2026-09-06)

**Verified rate from Azure Retail Prices API** (`effectiveStartDate: 2021-06-08`):

| SKU | Rate | Unit |
|---|---|---|
| B1 Linux | **$0.018** | per hour |

**30-day calculation (720 hours):**

| Tier | Calculation | Cost |
|---|---|---|
| B1 Linux plan | 720 × $0.018 | **$12.96/month** |

App Service billing is flat-rate per plan tier — no per-second metering, no idle discount, no free grant. The B1 plan cost is fixed regardless of activity level.

This is slightly higher than ACA's best-case cost (~$10/month) and within the range of the realistic ACA cost (~$11–13/month). The price difference is negligible, but App Service offers no meaningful advantage to offset its higher migration cost.

### Migration Cost

**Very high.** Requires:
- New `Microsoft.Web/serverfarms` (App Service Plan) Bicep resource
- New `Microsoft.Web/sites` (Web App) Bicep resource
- Decommissioning existing ACA resources (`Microsoft.App/containerApps`, potentially the shared `Microsoft.App/managedEnvironments`)
- Re-wiring all environment variables, managed identity, and observability
- Switching from `az containerapp` deployment tooling to `az webapp` tooling

There is no functional or cost benefit that justifies this migration from the already-provisioned ACA shape.

---

## 5. Option 4: Brief Notes on AKS and Azure VMs

### Azure Kubernetes Service (AKS)

AKS provides the richest container orchestration: full managed identity support, excellent observability via Azure Monitor, and Kubernetes-native liveness/readiness probes. However, for a **single, lightweight, always-on process**, AKS is severe overkill:

- Minimum viable cluster: 1 system node pool of 1 × Standard_B2s (~$35–70/month in Sweden Central) plus control-plane overhead
- Cluster management burden: node OS upgrades, node pool management, API server maintenance windows
- No advantage over ACA in managed identity, observability, or restart behaviour for a 1-replica workload
- Significant migration cost from ACA

**Verdict: Not recommended.**

### Azure Virtual Machine

An Azure VM running the .NET process natively or in Docker would cost at minimum ~$15–30/month for a B1s/B2s VM in Sweden Central, require OS patch management, manual container lifecycle management, and provide no benefit over ACA for this workload.

**Verdict: Dismissed.**

---

## 6. Comparison Table

| Dimension | **ACA Consumption** (current) | **ACI Standard** | **App Service B1 Linux** | **AKS** |
|---|---|---|---|---|
| **30-day cost (0.5 vCPU / 1 GiB, Sweden Central)** | ~$10–13/month | ~$20.46/month | $12.96/month | $35–70+/month |
| **Billing model** | Per-second, idle/active tiers | Per-second, flat rate | Flat hourly (plan tier) | Per-node flat |
| **Free grant** | 180k vCPU-s / 360k GiB-s (not material for always-on) | None | N/A | N/A |
| **User-assigned MI** | ✅ Already wired | ✅ Supported | ✅ Supported | ✅ Supported |
| **`AZURE_CLIENT_ID` pattern** | ✅ Already wired | ✅ Works identically | ✅ Works identically | ✅ Works |
| **Auto-restart on crash** | ✅ Automatic (container exit) | ✅ With `restartPolicy: Always` | ✅ Worker process restart | ✅ Kubernetes restart |
| **Rolling deploy (no-gap)** | ✅ Revision-based single mode | ❌ Stop/recreate group | ❌ Requires Standard tier slot | ✅ Rolling update |
| **Liveness probes** | ✅ HTTP or TCP | ❌ None | ✅ HTTP health check | ✅ Full Kubernetes probes |
| **Log Analytics integration** | ✅ Already wired (environment level) | ⚠️ Manual / sidecar only | ⚠️ Different integration path | ⚠️ Different integration path |
| **App Insights integration** | ✅ Already wired (env var) | ✅ SDK-only | ✅ Built-in portal integration | ✅ SDK-only |
| **Migration cost from current ACA** | **Zero** (wire `command`/`args` only) | High (new resource type) | Very high (new plan + app resource) | Very high |
| **Operational complexity** | Low (managed platform) | Low–medium | Low–medium | High |

---

## 7. Recommendation

### **Use Option 1: ACA Consumption-plan always-on (current shape)**

The recommendation is unambiguous: **stay on Azure Container Apps, Consumption plan, with `minReplicas: 1`**. The bicep change required is a single `command`/`args` addition to the existing container template in `orchestratorApp`.

**Rationale:**

1. **Zero migration cost.** The ACA environment, Container App resource, user-assigned managed identity, Log Analytics wiring, Application Insights env var, and all backing service env vars are already provisioned and wired in `infra/main.bicep`. No new resource types, no new deployment tooling, no re-wiring of identity or observability.

2. **Cheapest viable option.** At ~$10–13/month, ACA is less expensive than ACI ($20.46/month) and comparable to App Service ($12.96/month), while using a deployment model the repo already owns.

3. **ADR 0004-compliant identity, already wired.** User-assigned managed identity with `AZURE_CLIENT_ID` is already in the Bicep and verified against the ACA managed identity docs. No identity changes needed.

4. **Restart behaviour is adequate for the workload.** Automatic container restart on crash satisfies availability requirements. The Cosmos ETag lease handles the transient gap between crash and restart. At a 5–60 minute sweep cadence, even a 30-second restart latency is operationally negligible.

5. **Observability already complete.** Log Analytics and Application Insights are wired at the environment and container levels. No new observability infrastructure or configuration is required.

6. **Rolling revisions at no extra cost.** `activeRevisionsMode: 'Single'` already in the Bicep gives near-zero-downtime image deploys without deployment slots.

**Recommended bicep change:** Add the following to the `containers` array entry in `orchestratorApp`:

```bicep
command: ['dotnet', 'DarkSoftwareFactory.Runtime.dll']
args: ['serve-orchestrator']
```

> The exact entrypoint path depends on the container image's published path. Confirm with the runtime Dockerfile's `ENTRYPOINT`/`CMD` before wiring.

---

## 8. Gaps and Uncertainties

| Gap | Details | Impact |
|---|---|---|
| **ACA restart latency SLA** | The docs confirm automatic restart on crash but do not specify a latency SLA. In practice ACA restarts are typically seconds for a cached image. | Low — the Cosmos ETag lease handles any transient gap. |
| **ACA "active" billing threshold in practice** | The 0.01 vCPU active threshold is documented. Whether .NET timer sleep (`Task.Delay`) reliably stays below this threshold is not verified from a primary source. GC or timer infrastructure spikes could trigger the active rate. | Low — even at 100% active rate the workload remains affordable. |
| **Free grant: idle vs active pooling** | The pricing page states the free grant applies to vCPU-seconds and GiB-seconds but does not explicitly confirm whether idle and active seconds draw from the same pool. Cost estimates above assume they do. | Low — the free grant saves at most ~$1.62/month for this workload regardless. |
| **ACI memory unit (GB vs GiB)** | The ACI API meter uses "GB Hour" (SI gigabyte) while ACA uses "GiB Second" (IEC gibibyte). ACI cost above uses 1 GB ≈ 1 GiB (~2.4% error). | Negligible. |
| **App Service "Always On" with no HTTP listener** | The "Always On" feature pings the app's HTTP endpoint to keep it warm. For a container with no HTTP port, behaviour was not explicitly verified from a primary source. | Low for ACA path (irrelevant); medium if App Service is ever reconsidered. |
| **ACA platform-maintenance restarts** | ACA may restart replicas during node maintenance or OS patching. Frequency and timing are not documented. The Cosmos ETag lease guards against double-triggering during these events. | Low. |
| **Exact `serve-orchestrator` entrypoint** | The bicep change requires the container image's correct entrypoint path. Must be confirmed from the runtime Dockerfile. | Implementation-blocking but not a hosting-decision blocker. |
