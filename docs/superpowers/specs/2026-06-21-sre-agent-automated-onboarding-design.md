# Automate Azure SRE Agent onboarding in `dsf new` (Phase 3, part A)

Date: 2026-06-21
Status: Draft (for review)
Supersedes: ADR 0009 (render-only onboarding; "no headless path")
Relates to: ADR 0004 (ACA runtime), ADR 0006 (Azure data adapters), ADR 0013 (SRE/council feedback)

## Goal

Make `dsf new` actually stand up the Azure SRE Agent for a product instead of just
rendering a manual runbook: create the agent, grant its managed identity the right RBAC on
both the factory resource group and the monitored-app resource group, wire it to Azure
Monitor, and connect the product GitHub repo. The previous render-only step existed because
ADR 0009 (June 18) concluded headless onboarding was not achievable. That premise is now
obsolete: Microsoft ships `Microsoft.App/agents` as an ARM resource with Bicep templates, a
two-plane REST API, and a `targetRGs` RBAC parameter.

## Background (what the platform supports now)

- The SRE agent is an ARM resource: `Microsoft.App/agents`. It is deployable via Bicep.
- A user-assigned managed identity (UAMI) is the agent's Azure identity. Granting it
  `Reader` + `Monitoring Reader` + `Log Analytics Reader` on a resource group lets the agent
  query Azure Monitor / Log Analytics / Application Insights for everything in that group.
  `Monitoring Contributor` at subscription scope lets it manage the alert lifecycle.
- Deployment is two-phase. Phase 1 (ARM/Bicep): agent, UAMI, role assignments, and
  connectors. Phase 2 (data plane, per-agent endpoint with audience `https://azuresre.dev`):
  repo connection, hooks, knowledge. ARM cannot do Phase 2 yet.
- Region constraint: the agent resource only exists in Sweden Central, East US 2, and
  Australia East. Once deployed it can monitor resource groups in any region.
- Creating the agent and assigning RBAC requires the caller to hold `Owner`, or
  `Contributor` + `User Access Administrator`, on the subscription.

## Decisions (from brainstorming)

1. Monitored-RG topology: separate RG, DSF-provisioned. `targetRGs` = factory RG + the
   monitored-app RG(s), deduped.
2. Provisioning path: native DSF Bicep (a new `infra/sre-agent.bicep`), driven by the
   provisioner's injected `run` callable. Not the `microsoft/sre-agent` bash scripts.
3. GitHub connection: automated via a token DSF already manages (the squad GitHub token in
   Key Vault, or a Bring-Your-Own GitHub App), as a Phase-2 data-plane call. No interactive
   OAuth.
4. Permission level defaults to `Reader` (read-only; agent requests elevation per action).
   `Privileged` is a later opt-in.
5. The agent lives in its own resource group (`rg-dsf-sre-{product}`, matching the repo's
   `rg-dsf-{product}` convention) in a supported
   region (`sre_agent_location`, default `swedencentral`), independent of the product's
   region. Sweden Central is the only EU region among the three the agent supports, so it is
   the EU-data-residency default.

## Components

### 1. `infra/sre-agent.bicep` (new, `targetScope = 'subscription'`)

Subscription-scoped because role assignments span more than one resource group.

Parameters:
- `product` (string)
- `agentName` (string, e.g. `dsf-sre-<product>`)
- `sreAgentLocation` (string; one of the three supported regions)
- `agentResourceGroup` (string; the agent's own RG)
- `targetResourceGroups` (array of RG names to monitor: factory RG + monitored-app RG(s))
- `appInsightsId` (string; the factory App Insights resource id, from `main.bicep` output)
- `logAnalyticsId` (string; the factory Log Analytics workspace id, from `main.bicep` output)
- `permissionLevel` (string; `Reader` default, `Privileged` opt-in)

Declares:
- the agent's RG (created if absent) and a UAMI in it;
- the `Microsoft.App/agents` resource bound to that UAMI;
- role assignments per target RG (`Reader`, `Monitoring Reader`, `Log Analytics Reader`;
  plus the `Privileged` contributor roles when `permissionLevel == 'Privileged'`), and
  `Monitoring Contributor` at subscription scope;
- Log Analytics + Application Insights connectors (ARM sub-resources) pointed at the
  factory's workspace/component, so the Azure Monitor link is explicit and verifiable.

Outputs: `agentId`, `agentEndpoint` (the data-plane URL, `properties.agentEndpoint`),
`agentPrincipalId`.

Cross-RG role assignments use per-RG modules (a small `modules/rg-role-assignments.bicep`
scoped to each target RG) because a role assignment resource is scoped to its RG.

### 2. Provisioner step: `onboard_sre_agent` -> `deploy_sre_agent`

In `cli/src/dsf/instance/provisioner.py`, the late step becomes a real deploy with two
actions through the injected `run`:

1. Phase 1: `az deployment sub create -l <sreAgentLocation> -f infra/sre-agent.bicep -p ...`
   passing the parameters above (target RGs and App Insights/LAW ids come from the
   `provision_azure` deployment outputs already captured on the manifest).
2. Phase 2 (repo connect): fetch a data-plane token
   (`az account get-access-token --resource https://azuresre.dev`), read the agent endpoint
   from the Phase-1 outputs, and POST the repo connection to the agent endpoint using the
   squad GitHub token (resolved from Key Vault, the same token `_seed_github_token` handles)
   or a configured BYO GitHub App. If the data-plane token or GitHub token is unavailable,
   the step records what it skipped (mirroring `apply-extras.sh` behavior) rather than
   failing the whole provision.

`execute=False` remains a side-effect-free preview: it records the planned commands and
writes the manifest, runs no `az`.

### 3. `InstanceSpec` additions (`cli/src/dsf/instance/spec.py`)

- `sre_agent_location: str = "swedencentral"` validated against the live provider region
  set (2026-06: swedencentral, uksouth, eastus2, australiaeast, francecentral,
  canadacentral, koreacentral). Sweden Central is the EU default. (Issue #63.)
- `monitored_resource_groups: list[str] = []`; the effective monitored set is
  `[resource_group(), *monitored_resource_groups]` deduped, so single-RG products work with
  no extra config and a separate app RG is opt-in.
- A helper `sre_agent_name()` -> `dsf-sre-<product>` and `sre_resource_group()` ->
  `rg-dsf-sre-<product>`.

### 4. ADR + docs

- New ADR (next number) superseding ADR 0009: DSF now provisions the SRE agent via Bicep
  with `targetRGs` RBAC and a data-plane repo connect; documents the region constraint and
  the subscription-level RBAC prerequisite. Mark ADR 0009 superseded.
- `render_sre_onboarding` shrinks from a full manual runbook to a short post-deploy summary:
  what got created, the agent portal link, and the one-time `what-if`/verification note.
  Drop the "onboarding is interactive (wizard + OAuth)" framing.

## Data flow

```
dsf new --execute
  provision_azure        -> main.bicep into factory RG; outputs LAW id, App Insights id, RG
  ...
  deploy_sre_agent
    Phase 1: az deployment sub create -f infra/sre-agent.bicep
             -> agent RG + UAMI + Microsoft.App/agents
             -> role assignments on [factory RG, app RG] + subscription Monitoring Contributor
             -> Log Analytics + App Insights connectors
             -> outputs: agentEndpoint
    Phase 2: az account get-access-token --resource https://azuresre.dev
             POST {agentEndpoint}/.../repos  (GitHub token from Key Vault / BYO App)
  write_config            -> manifest records agent id + endpoint
```

## Error handling

- Region: `InstanceSpec` validation rejects an unsupported `sre_agent_location` before any
  Azure call.
- RBAC denial: if `az deployment sub create` fails on role assignment (caller lacks
  `User Access Administrator`/`Owner`), surface the Azure error and the documented
  prerequisite; do not swallow it.
- Phase 2 best-effort: a missing data-plane or GitHub token records a skip with the exact
  manual follow-up, and the rest of the provision still succeeds (the agent is usable for
  Azure Monitor without the repo connection).
- `dsf new` already persists the manifest even when a step raises, so a retry reuses names.

## Testing

- Provisioner tests (offline, injected `run`): assert the `az deployment sub create` command
  and parameters, the target-RG dedup, the data-plane token command, and the repo-connect
  POST shape. No live Azure.
- Spec tests: `sre_agent_location` validation; `monitored_resource_groups` dedup;
  `sre_agent_name()`/`sre_resource_group()`.
- Bicep is `what-if`-validated manually (noted in the summary); not run in CI.
- `make test` stays green; no eval/live dependencies added.

## Out of scope (flagged, not done here)

- Phase B: querying the agent's incidents/threads via its MCP server and feeding metrics and
  signals back to the Feature Council (self-improving loop).
- `Privileged` permission level beyond the parameter hook.
- Pre-existing stragglers from the offline-removal work that touch `main.bicep`
  (`DSF_MODE=azure` env, the Event Grid -> Service Bus push ingestion module, the seeded
  `dry_run` feature flag) are now dead. They want a separate cleanup pass and are not folded
  in here, to keep this change focused.

## Risks / call-outs

- The `Microsoft.App/agents` ARM schema and connector sub-resource shapes are newish; the
  Bicep may need an api-version bump as the resource provider stabilizes. The
  `microsoft/sre-agent` templates are the reference.
- The data-plane repo-connect endpoint path is read from Microsoft's API reference at
  implementation time; if it is not yet stable, Phase 2 falls back to the recorded-skip path
  and a documented manual connect, without blocking Phase 1.
