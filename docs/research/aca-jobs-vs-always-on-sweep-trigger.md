---
title: "Azure Container Apps scheduled Jobs vs always-on Container App BackgroundService for periodic sweep triggering"
date: 2026-09-05
issue: 170
status: draft
---

# Azure Container Apps scheduled Jobs vs always-on Container App `BackgroundService` for periodic sweep triggering

## Scope and source notes

- This note compares two hosting shapes for DSF's low-frequency, pull-only council sweep: a scheduled Azure Container Apps Job and an always-on Azure Container App that hosts a .NET `BackgroundService` polling loop. Azure platform claims are cited to Microsoft Learn or a first-party Microsoft API, and DSF implementation claims are cited to this repository on GitHub. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ACA pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/), [Azure Retail Prices API](https://prices.azure.com/api/retail/prices?$filter=serviceName%20eq%20%27Azure%20Container%20Apps%27%20and%20armRegionName%20eq%20%27swedencentral%27), [DSF repo](https://github.com/JoranBergfeld/dark-software-factory))
- The requested `BackgroundService` URL (`https://learn.microsoft.com/en-us/dotnet/core/extensions/background-service`) returned 404 on 2026-09-05, so the closest current first-party replacements used here are the .NET Worker Services and Generic Host docs. ([Workers in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/workers), [Generic Host](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host))

## 1. Azure Container Apps Jobs — how they work

### What a scheduled Job is vs a manual or event-driven Job

Azure Container Apps has two compute resource families: **apps** and **jobs**. Apps run continuously and restart failed containers automatically. Jobs start, run for a finite duration, and stop when finished; each execution is a single run of the job definition. Jobs can be triggered **manually**, on a **schedule**, or by **events**. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs))

- **Manual** jobs are started on demand from the Azure CLI, portal, or ARM REST API. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ACA jobs quickstart](https://learn.microsoft.com/en-us/azure/container-apps/jobs-get-started-cli))
- **Schedule** jobs use `triggerType: "Schedule"` plus `scheduleTriggerConfig.cronExpression` and run automatically on the configured cron schedule. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [Microsoft.App/jobs template reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/jobs))
- **Event** jobs use KEDA-backed event scaling and start executions in response to supported event sources. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ACA scale rules](https://learn.microsoft.com/en-us/azure/container-apps/scale-app))

### `triggerType: Schedule` and cron

Scheduled jobs use standard five-field cron syntax: minute, hour, day-of-month, month, day-of-week. Microsoft’s examples include `*/5 * * * *` for every five minutes and `0 */2 * * *` for every two hours, and the docs explicitly state that scheduled-job cron expressions are evaluated in **UTC**. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs))

### Relationship to the existing Container Apps Environment

Jobs and apps run in the same **Container Apps Environment**, and the docs explicitly say that they can share capabilities such as networking and logging. The CLI quickstart also states that the environment is an isolation boundary around container apps and jobs so they can share the same network and communicate with each other. DSF already provisions a `Microsoft.App/managedEnvironments@2025-01-01` resource named `containerEnv`, so a scheduled job can live beside the existing `orchestratorApp` instead of requiring a separate environment. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ACA jobs quickstart](https://learn.microsoft.com/en-us/azure/container-apps/jobs-get-started-cli), [DSF `infra/main.bicep`](https://github.com/JoranBergfeld/dark-software-factory/blob/main/infra/main.bicep), [ADR 0004](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0004-azure-container-apps-runtime.md))

### Managed identity support, including user-assigned identity

`Microsoft.App/jobs` supports the same Azure managed identity shape as `Microsoft.App/containerApps`: an `identity` block with `type` and `userAssignedIdentities`. The ACA managed identity docs explain the difference between system-assigned and user-assigned identities, and the ARM/Bicep examples show the `UserAssigned` form. The docs also state that when the Azure Identity client library is used with a user-assigned identity, the client ID must be specified explicitly. That matches DSF’s ADR 0004 design, which attaches a user-assigned identity and passes `AZURE_CLIENT_ID`. ([Microsoft.App/jobs template reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/jobs), [ACA managed identity](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity), [ADR 0004](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0004-azure-container-apps-runtime.md), [DSF `infra/main.bicep`](https://github.com/JoranBergfeld/dark-software-factory/blob/main/infra/main.bicep))

### `replicaTimeout`, `replicaCompletionCount`, and `parallelism`

ACA jobs expose the core execution-shape knobs directly:

| Setting | Meaning |
| --- | --- |
| `replicaTimeout` | Maximum seconds a replica is allowed to run before ACA terminates it. |
| `parallelism` | Number of replicas to run per execution. |
| `replicaCompletionCount` | Number of replicas that must complete successfully for the execution to succeed. |

The docs recommend `parallelism: 1` and `replicaCompletionCount: 1` for most jobs, which is the natural fit for “run one sweep” semantics. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [Microsoft.App/jobs template reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/jobs))

### Retry policy and execution history retention

ACA jobs support `replicaRetryLimit`, defined as the maximum number of times to retry a failed replica; setting it to `0` disables retries. Microsoft also states that `replicaTimeout` takes precedence if the timeout expires before all retries occur. Each job maintains recent execution history, and the docs explicitly cap **scheduled and event-based** job execution history at the most recent **100 successful and failed executions**; older detail should be obtained from the configured logs provider. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs))

## 2. Cold-start / latency

### Scale-to-zero shape

A scheduled job starts a fresh execution when the cron schedule fires, runs until completion, and then goes back to zero running executions. By contrast, a container app with `minReplicas: 1` is intentionally kept alive; ACA billing guidance says an app that scales to zero has no usage charges, but an app pinned to a minimum replica can still incur reduced idle charges while inactive. DSF’s current `orchestratorApp` is pinned to `minReplicas: 1` and `maxReplicas: 1`, so it is explicitly the always-on shape. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ACA scaling](https://learn.microsoft.com/en-us/azure/container-apps/scale-app), [ACA pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/), [DSF `infra/main.bicep`](https://github.com/JoranBergfeld/dark-software-factory/blob/main/infra/main.bicep))

### Startup latency for a job execution

The docs fetched for this note do **not** publish a fixed cold-start SLA or a representative startup-time number for ACA jobs. What they do make clear is that jobs start a new execution, that billing begins at execution start, and that each execution runs a finite-duration container workload; in practice the startup path includes scheduling the execution, container startup, and app initialization, with image-pull time depending on cache state and image locality. Because Microsoft does not publish a standard number here, any “typical” startup figure should be treated as an engineering assumption rather than a documented platform guarantee. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ACA pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/), [Workers in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/workers))

For DSF’s use case, that matters less than it would for request/response serving. A council sweep that runs every **5–60 minutes** is not latency-sensitive in the HTTP sense; even a conservative **~30 second assumed startup penalty** would add about **10%** overhead to a 5-minute interval and about **0.8%** overhead to a 60-minute interval. Those percentages are arithmetic on the scheduling interval, not Azure-published latency numbers. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs))

### Consumption vs dedicated implications

The Consumption plan is the fit-for-purpose plan for low-duty-cycle jobs because apps and jobs are billed per second, and jobs incur no usage charges when not running. The Dedicated plan is different: pricing is based on provisioned dedicated workload profile instances plus a management fee, so it is better for steady, reserved capacity than for an infrequent periodic sweep. The `Microsoft.App/jobs` resource also exposes `workloadProfileName`, which is the hook used when pinning a job to a specific dedicated workload profile. ([ACA pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/), [Microsoft.App/jobs template reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/jobs))

## 3. Cost at low cadence

### ACA pricing model

The Container Apps pricing page states that Consumption-plan apps are billed on per-second resource allocation, and that the first **180,000 vCPU-seconds**, **360,000 GiB-seconds**, and **2 million requests** per subscription per month are free. It also states that jobs are billed at the **active** rate from execution start to completion and incur **no usage charges when not running**, while pinned app replicas can incur a reduced **idle** rate when inactive. ([ACA pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/))

The pricing page fetched through `web_fetch` does not expose numeric per-second rates in the rendered table, so the exact rate numbers below come from Microsoft’s first-party **Azure Retail Prices API** for **Sweden Central** on 2026-09-05. Those rates are:

- Active vCPU: **$0.000024 / vCPU-second**. ([Azure Retail Prices API](https://prices.azure.com/api/retail/prices?$filter=serviceName%20eq%20%27Azure%20Container%20Apps%27%20and%20armRegionName%20eq%20%27swedencentral%27))
- Active memory: **$0.000003 / GiB-second**. ([Azure Retail Prices API](https://prices.azure.com/api/retail/prices?$filter=serviceName%20eq%20%27Azure%20Container%20Apps%27%20and%20armRegionName%20eq%20%27swedencentral%27))
- Idle vCPU: **$0.000003 / vCPU-second**. ([Azure Retail Prices API](https://prices.azure.com/api/retail/prices?$filter=serviceName%20eq%20%27Azure%20Container%20Apps%27%20and%20armRegionName%20eq%20%27swedencentral%27))
- Idle memory: **$0.000003 / GiB-second**. ([Azure Retail Prices API](https://prices.azure.com/api/retail/prices?$filter=serviceName%20eq%20%27Azure%20Container%20Apps%27%20and%20armRegionName%20eq%20%27swedencentral%27))

For a **0.5 vCPU / 1 GiB** container, that yields:

- **Job active run cost** = `0.5 × 0.000024 + 1 × 0.000003 = $0.000015 / second`. ([Azure Retail Prices API](https://prices.azure.com/api/retail/prices?$filter=serviceName%20eq%20%27Azure%20Container%20Apps%27%20and%20armRegionName%20eq%20%27swedencentral%27))
- **Always-on app idle cost** = `0.5 × 0.000003 + 1 × 0.000003 = $0.0000045 / second` while the replica is inactive. ([Azure Retail Prices API](https://prices.azure.com/api/retail/prices?$filter=serviceName%20eq%20%27Azure%20Container%20Apps%27%20and%20armRegionName%20eq%20%27swedencentral%27), [ACA pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/))

### Corrected duty-cycle math

The important correction is: **a 5-minute job that actually runs for 5 minutes every 5 minutes is at 100% duty cycle, not 5% duty cycle**. The duty cycle is `run time / interval`, not `run time / day`. At the other end, a 5-minute schedule with a 30-second sweep is a **10%** duty cycle, and a 60-minute schedule with a 5-minute sweep is an **8.33%** duty cycle. (Arithmetic from the interval assumptions above; billing inputs from the cited ACA pricing sources.)

### Concrete daily and 30-day cost math

Assumptions below:

- One replica. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs))
- 0.5 vCPU / 1 GiB. ([DSF `infra/main.bicep`](https://github.com/JoranBergfeld/dark-software-factory/blob/main/infra/main.bicep))
- Always-on app is active only during the sweep and otherwise billed at the idle rate; that matches the pricing page’s active/idle split better than pretending the app is billed at the active rate 24x7. ([ACA pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/))
- Job incurs only active charges during its run window. ([ACA pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/))

| Scenario | Active seconds / day | Duty cycle | Always-on app gross cost / 30d | Scheduled job gross cost / 30d | Notes |
| --- | ---: | ---: | ---: | ---: | --- |
| Every 5 min, 30s sweep | 8,640 | 10.0% | **$14.39** | **$3.89** | Job active usage is 129,600 vCPU-s and 259,200 GiB-s / month, both inside the free grant if the subscription has no other ACA usage. |
| Every 5 min, 5 min sweep | 86,400 | 100% | **$38.88** | **$38.88** | No meaningful duty-cycle savings; the job never really gets a rest between executions. |
| Every 60 min, 30s sweep | 720 | 0.833% | **$11.89** | **$0.32** | Huge win for jobs; active usage is tiny and well inside the free grant. |
| Every 60 min, 5 min sweep | 7,200 | 8.33% | **$13.93** | **$3.24** | Job still clearly cheaper because it avoids 55 minutes of idle replica time every hour. |

The free-grant takeaway is straightforward: an always-on app with a pinned `minReplicas: 1` allocates resources all month, so it blows past the monthly free grant quickly, while a scheduled job often stays within the grant at 30-second or even 5-minute hourly sweeps. By contrast, a 5-minute schedule with a full 5-minute sweep is effectively continuous work and loses most of the cost advantage. ([ACA pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/), [Azure Retail Prices API](https://prices.azure.com/api/retail/prices?$filter=serviceName%20eq%20%27Azure%20Container%20Apps%27%20and%20armRegionName%20eq%20%27swedencentral%27))

## 4. Observability (Application Insights + Log Analytics)

### Log Analytics in ACA

ACA environments can be configured to send application and system logs to **Azure Monitor Log Analytics**, and DSF already provisions the Container Apps environment with `appLogsConfiguration.destination: 'log-analytics'` pointing at its Log Analytics workspace. The ACA logging docs describe three log categories: **console logs**, **system logs**, and optional **HTTP logs**. ([ACA observability](https://learn.microsoft.com/en-us/azure/container-apps/observability), [ACA logging](https://learn.microsoft.com/en-us/azure/container-apps/logging), [ACA log monitoring](https://learn.microsoft.com/en-us/azure/container-apps/log-monitoring), [DSF `infra/main.bicep`](https://github.com/JoranBergfeld/dark-software-factory/blob/main/infra/main.bicep))

- Console/stdout/stderr logs are queried from `ContainerAppConsoleLogs_CL`. ([ACA log monitoring](https://learn.microsoft.com/en-us/azure/container-apps/log-monitoring))
- System/service logs are queried from `ContainerAppSystemLogs_CL`. ([ACA log monitoring](https://learn.microsoft.com/en-us/azure/container-apps/log-monitoring), [ACA logging](https://learn.microsoft.com/en-us/azure/container-apps/logging))
- The jobs quickstart shows filtering a single job run’s console logs by the execution name prefix in `ContainerAppConsoleLogs_CL`. ([ACA jobs quickstart](https://learn.microsoft.com/en-us/azure/container-apps/jobs-get-started-cli))

One small documentation wrinkle: the jobs quickstart query uses `ContainerGroupName_s`, while the log-monitoring schema table lists `ContainerGroupName_g`/replica-name style fields. The operational point is still the same: Log Analytics stores per-replica/per-execution console output, and job runs can be narrowed to a single execution by job-run name. ([ACA jobs quickstart](https://learn.microsoft.com/en-us/azure/container-apps/jobs-get-started-cli), [ACA log monitoring](https://learn.microsoft.com/en-us/azure/container-apps/log-monitoring))

### Application Insights behavior

ACA does **not** provide the Application Insights auto-instrumentation agent for Container Apps; the official observability doc says you must instrument your app code with an Application Insights SDK. The Application Insights overview likewise says the normal code-based path is: create an Application Insights resource, get its connection string, add the SDK/OpenTelemetry distro, and configure the connection string. DSF already injects `APPLICATIONINSIGHTS_CONNECTION_STRING` into the current container app, and the `Microsoft.App/jobs` template supports the same container `env` array, so a scheduled job can carry the same setting. ([ACA observability](https://learn.microsoft.com/en-us/azure/container-apps/observability), [Application Insights overview](https://learn.microsoft.com/en-us/azure/azure-monitor/app/app-insights-overview), [Microsoft.App/jobs template reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/jobs), [DSF `infra/main.bicep`](https://github.com/JoranBergfeld/dark-software-factory/blob/main/infra/main.bicep))

### `operation_Id` and telemetry boundaries

The docs gathered here do **not** say that ACA automatically assigns a unique Application Insights `operation_Id` per job execution. Because ACA does not auto-instrument Application Insights for you, correlation boundaries still depend on what the application emits. The practical difference is architectural:

- In a **scheduled job**, each sweep is a fresh process execution, so there is a natural per-run boundary for logs, exit code, and any root Activity/operation the code decides to start. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ACA observability](https://learn.microsoft.com/en-us/azure/container-apps/observability))
- In an **always-on `BackgroundService`**, many sweep ticks happen inside one long-lived host process. Unless the code explicitly starts a new root Activity/operation for each tick, the platform itself does not create a per-tick execution object the way ACA jobs do. DSF’s `PeriodicSweepService` currently logs a completed run and keeps looping. ([Workers in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/workers), [Generic Host](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host), [DSF `PeriodicSweepService.cs`](https://github.com/JoranBergfeld/dark-software-factory/blob/main/dotnet/src/Dsf.Runtime/PeriodicSweepService.cs))

Operationally, that makes job executions the cleaner unit for “one sweep = one observable run,” even if DSF still needs to instrument its own correlation identifiers inside the .NET code. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ACA observability](https://learn.microsoft.com/en-us/azure/container-apps/observability))

## 5. Failure and retry semantics

### Always-on `BackgroundService` approach

The current DSF worker uses `PeriodicSweepService : BackgroundService`, sets the default interval to **300 seconds**, runs the sweep inside a timer loop, and catches broad exceptions per tick. The catch block logs `"[dsf] orchestrator tick failed"` and then continues to the next timer tick, so one bad sweep does not crash the host. That design is intentional per the source comments. At the host/process level, ACA still treats the container app as a long-running app; if the process actually dies, the app shape is the one that restarts failed containers automatically. ([DSF `PeriodicSweepService.cs`](https://github.com/JoranBergfeld/dark-software-factory/blob/main/dotnet/src/Dsf.Runtime/PeriodicSweepService.cs), [ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [Workers in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/workers), [Generic Host](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host))

This gives resilience against transient per-tick errors, but it does **not** give a first-class platform object for “sweep execution #123 failed,” nor a built-in per-run retry budget. You mainly get logs inside a healthy-looking long-lived container. ([DSF `PeriodicSweepService.cs`](https://github.com/JoranBergfeld/dark-software-factory/blob/main/dotnet/src/Dsf.Runtime/PeriodicSweepService.cs), [ACA observability](https://learn.microsoft.com/en-us/azure/container-apps/observability))

### Scheduled Job approach

ACA jobs surface failure more explicitly. A job execution succeeds or fails as an execution, failed replicas can be retried via `replicaRetryLimit`, and recent execution status is visible through `az containerapp job execution list`, the ARM executions endpoint, and the portal’s execution history. The docs also state that `replicaTimeout` overrides retries if the timeout expires first. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ACA jobs quickstart](https://learn.microsoft.com/en-us/azure/container-apps/jobs-get-started-cli))

That is a materially better operational signal for the question “did the sweep fail **this run**?” because the platform gives you a failed execution record instead of only an error line embedded in a healthy worker process. Azure Monitor alerts are also part of ACA’s observability stack, so failed executions and their logs are much easier to wire into job-specific operational alerts than swallowed exceptions inside a permanently-running loop. ([ACA observability](https://learn.microsoft.com/en-us/azure/container-apps/observability), [ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs))

## 6. Interaction with ADR 0009 / ADR 0015 SRE fast-path

ADR 0009 and its replacement ADR 0015 define the Azure SRE Agent path as a separate, event-driven product integration that files issues or PRs into the product repo tagged `squad:ready`; the council sweep remains a separate, governed pull path. ADR 0014 also explicitly says DSF is **pull-only** and gets work by sweeping source agents on a schedule rather than exposing a push inbox. ([ADR 0009](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0009-leverage-azure-sre-agent.md), [ADR 0015](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0015-sre-agent-automated-onboarding.md), [ADR 0014](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0014-real-only-src-no-offline.md))

That means the hosting shape does **not** change the core SRE fast-path contract: the SRE Agent still writes to GitHub, and the council still notices those artifacts when it next performs a pull-based sweep. ([ADR 0009](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0009-leverage-azure-sre-agent.md), [ADR 0015](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0015-sre-agent-automated-onboarding.md), [ADR 0014](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0014-real-only-src-no-offline.md))

The hosting choice **does** affect an “urgent sweep now” option:

- With the current always-on loop, there is no documented external “run a sweep now” platform action; the worker wakes up on its own fixed timer. Because DSF intentionally has no inbound signal ingestion, there is no ingress-triggered fast lane into the council loop. ([ADR 0014](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0014-real-only-src-no-offline.md), [DSF `PeriodicSweepService.cs`](https://github.com/JoranBergfeld/dark-software-factory/blob/main/dotnet/src/Dsf.Runtime/PeriodicSweepService.cs))
- A scheduled ACA job can still be started **ad hoc** via `az containerapp job start` or the ARM `.../jobs/<job>/start` API, even when its normal trigger type is `Schedule`. That provides an operator-initiated urgent sweep without introducing inbound app traffic or violating the pull-only rule: the sweep still pulls state from GitHub/Azure; only the scheduler trigger becomes on-demand. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs))

Does that matter? Maybe not for the normal path, because ADR 0009/0015 intentionally keeps SRE incident filing distinct from council cadence. But it is a genuine platform capability advantage for jobs if DSF ever wants a manual “sweep right now after an incident” button without redesigning the runtime model. ([ADR 0015](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0015-sre-agent-automated-onboarding.md), [ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs))

## 7. Bicep / IaC shape for ACA Jobs

The official ARM/Bicep template reference for `Microsoft.App/jobs` lists current API versions including **`2024-03-01`**, **`2025-01-01`**, **`2025-07-01`**, and **`2026-01-01` latest**. For DSF, the most conservative shape is to stay aligned with the rest of `infra/main.bicep` and use `Microsoft.App/jobs@2025-01-01`; if issue #170 prefers the version explicitly requested in the prompt, `2024-03-01` is also a supported stable API version. ([Microsoft.App/jobs template reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/jobs))

A scheduled job’s core shape is:

```bicep
resource sweepJob 'Microsoft.App/jobs@2025-01-01' = {
  name: '${namePrefix}-sweep'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerEnv.id
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 1800
      replicaRetryLimit: 1
      scheduleTriggerConfig: {
        cronExpression: '*/5 * * * *'
        parallelism: 1
        replicaCompletionCount: 1
      }
    }
    template: {
      containers: [
        {
          name: 'sweep'
          image: runtimeImage
          command: ['dotnet', 'dsf-runtime.dll']
          args: ['sweep']
          env: [
            { name: 'AZURE_CLIENT_ID', value: runtimeIdentity.properties.clientId }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
            // ...same DSF env values as the current orchestrator app...
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
    }
  }
}
```

Every property in that sketch is grounded in the job resource reference: `environmentId`, `configuration.triggerType`, `configuration.scheduleTriggerConfig.cronExpression`, `configuration.replicaTimeout`, `configuration.replicaRetryLimit`, `identity`, and the usual container `env` array live on the job resource exactly where you would expect them. Jobs and apps can share the same `managedEnvironment`. ([Microsoft.App/jobs template reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/jobs), [ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs))

For DSF specifically, the identity attachment can be copied almost verbatim from the current container app, because ADR 0004 deliberately standardized on a user-assigned managed identity. The main behavior change is not IAM but **entrypoint shape**: the container must perform **one sweep and then exit** with a meaningful success or failure code, instead of hosting a forever loop. ([ADR 0004](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0004-azure-container-apps-runtime.md), [DSF `PeriodicSweepService.cs`](https://github.com/JoranBergfeld/dark-software-factory/blob/main/dotnet/src/Dsf.Runtime/PeriodicSweepService.cs), [Workers in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/workers))

## Findings summary

- **Recommendation: use an ACA scheduled Job for DSF’s periodic council sweep, not an always-on Container App loop.** The DSF workload is low-frequency, pull-only, and not latency-sensitive, which is the sweet spot for finite-duration scheduled jobs. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ADR 0014](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0014-real-only-src-no-offline.md))
- ACA jobs are a first-class fit for this shape: `triggerType: 'Schedule'`, five-field UTC cron, one execution per sweep, and shared use of the existing Container Apps Environment for networking and logging. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ACA jobs quickstart](https://learn.microsoft.com/en-us/azure/container-apps/jobs-get-started-cli))
- Jobs satisfy ADR 0004’s identity constraint: `Microsoft.App/jobs` supports **user-assigned managed identity**, so DSF can keep the same `AZURE_CLIENT_ID` / `DefaultAzureCredential` pattern it already uses. ([ACA managed identity](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity), [Microsoft.App/jobs template reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/jobs), [ADR 0004](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0004-azure-container-apps-runtime.md))
- Cost strongly favors jobs whenever the sweep does **not** run nearly continuously. At **every 5 minutes / 30 seconds** the job is about **$3.89 / 30d** vs about **$14.39 / 30d** for the always-on app; at **every 60 minutes / 5 minutes** the job is about **$3.24 / 30d** vs about **$13.93 / 30d**. Only the pathological **5 minutes every 5 minutes** case erases the savings. ([ACA pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/), [Azure Retail Prices API](https://prices.azure.com/api/retail/prices?$filter=serviceName%20eq%20%27Azure%20Container%20Apps%27%20and%20armRegionName%20eq%20%27swedencentral%27))
- Cold-start is the main tradeoff, but Azure’s docs do not publish a hard number and DSF’s 5–60 minute cadence makes that penalty operationally acceptable; a 30-second assumed startup penalty is small relative to the scheduling interval. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ACA pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/))
- Observability is cleaner with jobs: Log Analytics already captures ACA logs, each sweep becomes its own platform execution object, and Application Insights can be carried forward with the same connection-string env var pattern. The always-on loop can be instrumented too, but per-tick boundaries are not naturally surfaced by the platform. ([ACA observability](https://learn.microsoft.com/en-us/azure/container-apps/observability), [ACA log monitoring](https://learn.microsoft.com/en-us/azure/container-apps/log-monitoring), [Microsoft.App/jobs template reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/jobs))
- Failure signaling is materially better with jobs. The current `PeriodicSweepService` logs and swallows tick failures; ACA jobs give you `replicaRetryLimit`, explicit failed executions, and visible execution history, which is a better answer to “did this sweep fail?”. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [DSF `PeriodicSweepService.cs`](https://github.com/JoranBergfeld/dark-software-factory/blob/main/dotnet/src/Dsf.Runtime/PeriodicSweepService.cs))
- Jobs also add a useful operator escape hatch: even a scheduled job can be started on demand with `az containerapp job start`, which provides an urgent manual sweep without violating DSF’s pull-only ADR. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [ADR 0014](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0014-real-only-src-no-offline.md))
- Net: **for issue #170, DSF should model the council sweep as a scheduled ACA job that runs one sweep per execution and exits non-zero on failure.** That better matches the platform, cost model, operational visibility, and future “run now” control surface than keeping a permanently warm polling loop. ([ACA jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs), [Workers in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/workers), [Generic Host](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host))
