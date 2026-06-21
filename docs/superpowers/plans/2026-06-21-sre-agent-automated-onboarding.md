# Automate Azure SRE Agent onboarding — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `dsf new` provisions the Azure SRE Agent for a product (agent + UAMI + RBAC on the factory and monitored-app resource groups + Azure Monitor connectors), and connects the product GitHub repo, replacing the render-only `onboard_sre_agent` step.

**Architecture:** A new subscription-scoped Bicep (`infra/sre-agent.bicep`) declares the `Microsoft.App/agents` resource, its user-assigned managed identity, the cross-RG role assignments (via a per-RG module), and the Log Analytics + App Insights connectors. The provisioner's renamed `deploy_sre_agent` step runs it with `az deployment sub create` using outputs captured from the existing `provision_azure` deploy, then does a best-effort data-plane repo connect via `az rest`. Everything routes through the provisioner's injected `run` callable so it is offline-testable.

**Tech Stack:** Python 3.12, uv workspace, pydantic, Bicep / `az` CLI, pytest.

## Global Constraints

- `uv` only; run everything via `uv run`. ruff line length 100, rules `E,F,I,UP,B`.
- The provisioner never calls Azure directly; every external command goes through `self._run` (the injected callable) so tests stay offline.
- `execute=False` must remain side-effect-free (records planned commands, writes the manifest, runs no `az`).
- SRE agent regions are exactly `{"swedencentral", "eastus2", "australiaeast"}`; default `swedencentral` (EU).
- Resource-group naming follows the existing convention `rg-dsf-{product}` (the codebase hardcodes `dsf`, it does NOT use `name_prefix`). The agent RG is `rg-dsf-sre-{product}`.
- Built-in role definition GUIDs (verify against the portal if unsure): Reader `acdd72a7-3385-48ef-bd42-f606fba81ae7`, Monitoring Reader `43d0d8ad-25c7-4714-9337-8ba259a9fe05`, Log Analytics Reader `73c42c96-874c-492b-b04d-ab87d138a893`, Monitoring Contributor `749f88d5-cbae-40b8-bcfc-e573ddc772fa`.
- The gate after each task: `uv run ruff check . && uv run lint-imports && uv run pytest -q`.

---

### Task 1: `InstanceSpec` — SRE region + monitored RGs + name helpers

**Files:**
- Modify: `cli/src/dsf/instance/spec.py` (`InstanceSpec`, after line 42 / the validators / the helper methods near line 68-74)
- Test: `cli/tests/instance/test_spec.py` (create if absent; otherwise add to it)

**Interfaces:**
- Produces:
  - `InstanceSpec.sre_agent_location: str = "swedencentral"` (validated)
  - `InstanceSpec.monitored_resource_groups: list[str]` (default empty)
  - `InstanceSpec.sre_agent_name() -> str` → `f"dsf-sre-{product}"`
  - `InstanceSpec.sre_resource_group() -> str` → `f"rg-dsf-sre-{product}"`
  - `InstanceSpec.monitored_rgs() -> list[str]` → `[resource_group(), *monitored_resource_groups]`, order-preserving dedup

- [ ] **Step 1: Write the failing tests**

```python
# cli/tests/instance/test_spec.py
import pytest
from dsf.instance.spec import InstanceSpec


def _spec(**kw):
    return InstanceSpec(product="microbi", owner="acme", **kw)


def test_sre_agent_location_defaults_to_sweden_central():
    assert _spec().sre_agent_location == "swedencentral"


def test_sre_agent_location_rejects_unsupported_region():
    with pytest.raises(ValueError, match="sre_agent_location"):
        _spec(sre_agent_location="westeurope")


def test_sre_agent_name_and_rg():
    s = _spec()
    assert s.sre_agent_name() == "dsf-sre-microbi"
    assert s.sre_resource_group() == "rg-dsf-sre-microbi"


def test_monitored_rgs_defaults_to_factory_rg():
    assert _spec().monitored_rgs() == ["rg-dsf-microbi"]


def test_monitored_rgs_appends_and_dedupes():
    s = _spec(monitored_resource_groups=["rg-app", "rg-dsf-microbi", "rg-app"])
    assert s.monitored_rgs() == ["rg-dsf-microbi", "rg-app"]
```

- [ ] **Step 2: Run to verify they fail**

Run: `uv run pytest cli/tests/instance/test_spec.py -q`
Expected: FAIL (attributes/methods don't exist).

- [ ] **Step 3: Implement the spec changes**

Add fields after `location` (spec.py:41) and a validator alongside the others:

```python
    sre_agent_location: str = "swedencentral"
    monitored_resource_groups: list[str] = Field(default_factory=list)
```

```python
    @field_validator("sre_agent_location")
    @classmethod
    def _validate_sre_agent_location(cls, value: str) -> str:
        supported = {"swedencentral", "eastus2", "australiaeast"}
        if value not in supported:
            raise ValueError(
                f"sre_agent_location must be one of {sorted(supported)}, got {value!r}"
            )
        return value
```

Add helpers next to `resource_group()` (spec.py:68):

```python
    def sre_agent_name(self) -> str:
        """Azure SRE Agent resource name for this instance."""
        return f"dsf-sre-{self.product}"

    def sre_resource_group(self) -> str:
        """Dedicated resource group that hosts the SRE agent."""
        return f"rg-dsf-sre-{self.product}"

    def monitored_rgs(self) -> list[str]:
        """Resource groups the agent monitors: the factory RG plus any extras, deduped."""
        ordered = [self.resource_group(), *self.monitored_resource_groups]
        seen: set[str] = set()
        out: list[str] = []
        for rg in ordered:
            if rg not in seen:
                seen.add(rg)
                out.append(rg)
        return out
```

- [ ] **Step 4: Run to verify they pass**

Run: `uv run pytest cli/tests/instance/test_spec.py -q`
Expected: PASS.

- [ ] **Step 5: Gate + commit**

```bash
uv run ruff check . && uv run lint-imports && uv run pytest -q
git add cli/src/dsf/instance/spec.py cli/tests/instance/test_spec.py
git commit -m "feat(instance): SRE agent region + monitored RGs on InstanceSpec"
```

---

### Task 2: Bicep — `infra/sre-agent.bicep` + per-RG role module + main.bicep id outputs

**Files:**
- Create: `infra/sre-agent.bicep` (subscription-scoped)
- Create: `infra/modules/sre-rg-roles.bicep` (resource-group-scoped role assignments)
- Modify: `infra/main.bicep` (add two outputs near line 391)

**Interfaces:**
- Produces: a deployable subscription-scoped template taking `product, agentName, sreAgentLocation, agentResourceGroup, targetResourceGroups (array), appInsightsId, logAnalyticsId, permissionLevel`, emitting outputs `agentId`, `agentEndpoint`, `agentPrincipalId`.
- Consumes (Task 3 passes these): `appInsightsId`/`logAnalyticsId` from `main.bicep` outputs added here.

> Note on the `Microsoft.App/agents` resource: its exact `properties` shape and api-version are newish. Use the `microsoft/sre-agent` templates (https://github.com/microsoft/sre-agent/tree/main/sreagent-templates) and the ARM reference as the authoritative source for the agent resource and the Log Analytics / App Insights connector sub-resources. The skeleton below is structurally complete for identity, RBAC, and outputs; fill the agent resource body from that source.

- [ ] **Step 1: Add the two outputs to `main.bicep`**

After the existing outputs (main.bicep:391), add:

```bicep
@description('Application Insights resource id (consumed by the SRE agent connector + RBAC).')
output appInsightsId string = appInsights.id

@description('Log Analytics workspace resource id (consumed by the SRE agent connector + RBAC).')
output logAnalyticsId string = logAnalytics.id
```

- [ ] **Step 2: Create the per-RG role-assignment module**

```bicep
// infra/modules/sre-rg-roles.bicep
// Assign the SRE agent's managed identity the read roles on one target resource
// group. Deployed once per monitored RG by infra/sre-agent.bicep.
targetScope = 'resourceGroup'

@description('Principal (object) id of the SRE agent user-assigned managed identity.')
param principalId string

var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
var monitoringReaderRoleId = '43d0d8ad-25c7-4714-9337-8ba259a9fe05'
var logAnalyticsReaderRoleId = '73c42c96-874c-492b-b04d-ab87d138a893'

var roleIds = [
  readerRoleId
  monitoringReaderRoleId
  logAnalyticsReaderRoleId
]

resource roleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in roleIds: {
    name: guid(resourceGroup().id, principalId, roleId)
    properties: {
      principalId: principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    }
  }
]
```

- [ ] **Step 3: Create `infra/sre-agent.bicep`**

```bicep
// infra/sre-agent.bicep
// Provision the Azure SRE Agent for one product: a dedicated RG, a user-assigned
// managed identity, the Microsoft.App/agents resource, cross-RG read RBAC, the
// subscription-level Monitoring Contributor role, and Azure Monitor connectors.
// Deployed with: az deployment sub create -l <region> -f infra/sre-agent.bicep -p ...
targetScope = 'subscription'

param product string
param agentName string
param sreAgentLocation string
param agentResourceGroup string
@description('Resource groups the agent monitors (factory RG + monitored-app RG).')
param targetResourceGroups array
param appInsightsId string
param logAnalyticsId string
@allowed(['Reader', 'Privileged'])
param permissionLevel string = 'Reader'

var monitoringContributorRoleId = '749f88d5-cbae-40b8-bcfc-e573ddc772fa'

resource agentRg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: agentResourceGroup
  location: sreAgentLocation
}

module agentResources 'modules/sre-agent-resources.bicep' = {
  name: 'sre-agent-${product}'
  scope: agentRg
  params: {
    agentName: agentName
    location: sreAgentLocation
    appInsightsId: appInsightsId
    logAnalyticsId: logAnalyticsId
  }
}

// Read RBAC on every monitored resource group.
module rgRoles 'modules/sre-rg-roles.bicep' = [
  for rg in targetResourceGroups: {
    name: 'sre-roles-${rg}'
    scope: resourceGroup(rg)
    params: {
      principalId: agentResources.outputs.principalId
    }
  }
]

// Alert-lifecycle management at subscription scope.
resource monitoringContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, agentResources.outputs.principalId, monitoringContributorRoleId)
  properties: {
    principalId: agentResources.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringContributorRoleId)
  }
}

output agentId string = agentResources.outputs.agentId
output agentEndpoint string = agentResources.outputs.agentEndpoint
output agentPrincipalId string = agentResources.outputs.principalId
```

- [ ] **Step 4: Create `infra/modules/sre-agent-resources.bicep`** (RG-scoped: UAMI + agent + connectors)

```bicep
// infra/modules/sre-agent-resources.bicep
// The agent's own resources, deployed into its dedicated RG.
targetScope = 'resourceGroup'

param agentName string
param location string
param appInsightsId string
param logAnalyticsId string

resource agentIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${agentName}-id'
  location: location
}

// Microsoft.App/agents — confirm api-version + properties against the
// microsoft/sre-agent templates. The agent binds the UAMI above and registers the
// App Insights + Log Analytics connectors (sub-resources) for Azure Monitor access.
resource sreAgent 'Microsoft.App/agents@2025-02-02-preview' = {
  name: agentName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${agentIdentity.id}': {}
    }
  }
  properties: {
    // FILL from microsoft/sre-agent: model/provider config + connectors referencing
    // appInsightsId and logAnalyticsId.
  }
}

output principalId string = agentIdentity.properties.principalId
output agentId string = sreAgent.id
output agentEndpoint string = sreAgent.properties.agentEndpoint
```

- [ ] **Step 5: Validate the Bicep compiles (best-effort, offline)**

Run: `az bicep build --file infra/sre-agent.bicep` (if `az`+Bicep are installed).
Expected: compiles, or surfaces the exact `Microsoft.App/agents` schema warning to resolve against the reference. If `az` isn't available, do a structural review and rely on Task 3's provisioner test plus a manual `what-if` at deploy time. Record which you did.

- [ ] **Step 6: Commit**

```bash
git add infra/sre-agent.bicep infra/modules/sre-rg-roles.bicep infra/modules/sre-agent-resources.bicep infra/main.bicep
git commit -m "feat(infra): Bicep for SRE agent + cross-RG RBAC + Azure Monitor connectors"
```

---

### Task 3: Provisioner — `deploy_sre_agent` step (Phase 1 deploy + Phase 2 repo connect)

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py` (`plan()` step near line 188-193; `apply()` branch near line 279-289; add a `_sre_deploy_command` + `_connect_repo` helper)
- Test: `cli/tests/instance/test_provisioner.py`

**Interfaces:**
- Consumes: `spec.sre_agent_name()`, `spec.sre_resource_group()`, `spec.sre_agent_location`, `spec.monitored_rgs()` (Task 1); `azure_result.outputs["appInsightsId"]`, `["logAnalyticsId"]`, and (Phase 2) `["agentEndpoint"]`.
- Produces: the renamed step `deploy_sre_agent` and its recorded results (`"executed"` / `"deployed (dry-run)"` / a skip note).

- [ ] **Step 1: Write failing provisioner tests**

```python
# add to cli/tests/instance/test_provisioner.py — follow the file's existing
# recording-runner + spec fixtures.

def test_deploy_sre_agent_dry_run_records_plan(tmp_repo, spec):
    prov = Provisioner(spec, run=RecordingRunner(), repo_root=tmp_repo)
    manifest = prov.apply(execute=False)
    step = next(s for s in manifest.plan.steps if s.name == "deploy_sre_agent")
    assert "dry-run" in step.result
    assert step.executed is False


def test_deploy_sre_agent_executes_sub_deployment(tmp_repo, spec):
    runner = RecordingRunner(
        # provision_azure returns the ids the SRE deploy consumes
        outputs={"appInsightsId": "/sub/ai", "logAnalyticsId": "/sub/law",
                 "keyVaultName": "kv1"},
    )
    prov = Provisioner(spec, run=runner, repo_root=tmp_repo)
    prov.apply(execute=True)
    cmds = runner.commands
    sub = next(c for c in cmds if c[:4] == ["az", "deployment", "sub", "create"])
    joined = " ".join(sub)
    assert "infra/sre-agent.bicep" in joined
    assert f"agentName={spec.sre_agent_name()}" in sub
    assert f"sreAgentLocation={spec.sre_agent_location}" in sub
    # both monitored RGs are passed as the array param
    assert any("targetResourceGroups=" in part for part in sub)
```

> Adjust the fixtures/`RecordingRunner` shape to the file's existing conventions. If the existing tests use a different runner double, reuse it; the assertions (command prefix + params) are what matters.

- [ ] **Step 2: Run to verify they fail**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k sre -q`
Expected: FAIL (step still named `onboard_sre_agent`; no sub deployment).

- [ ] **Step 3: Rename the plan step**

In `plan()` (provisioner.py:188), rename and redescribe:

```python
            ProvisionStep(
                name="deploy_sre_agent",
                description=(
                    f"Provision the Azure SRE Agent for {s.product} "
                    f"(agent + RBAC on {', '.join(s.monitored_rgs())} + Azure Monitor)"
                ),
            ),
```

- [ ] **Step 4: Implement the apply() branch + helpers**

Replace the `onboard_sre_agent` branch (provisioner.py:279-289) with:

```python
                elif step.name == "deploy_sre_agent":
                    provisional = InstanceManifest(
                        spec=self.spec, plan=plan, executed=executed, azure=azure_result
                    )
                    render_sre_summary(provisional, repo_root=self._repo_root)
                    if not execute:
                        step.result = "deployed (dry-run)"
                    else:
                        outputs = azure_result.outputs if azure_result else {}
                        self._run(
                            self._sre_deploy_command(outputs), check=True
                        )
                        note = self._connect_repo(azure_result)
                        step.executed = True
                        step.result = note or "deployed"
```

Add the helpers near `_seed_github_token` (provisioner.py:367):

```python
    def _sre_deploy_command(self, outputs: dict[str, str]) -> list[str]:
        """`az deployment sub create` for the SRE agent, from provision_azure outputs."""
        s = self.spec
        bicep = str((self._repo_root or _default_repo_root()) / "infra" / "sre-agent.bicep")
        target_rgs = json.dumps(s.monitored_rgs())
        return [
            "az", "deployment", "sub", "create",
            "-l", s.sre_agent_location,
            "-n", f"dsf-sre-{s.product}",
            "-f", bicep,
            "-p",
            f"product={s.product}",
            f"agentName={s.sre_agent_name()}",
            f"sreAgentLocation={s.sre_agent_location}",
            f"agentResourceGroup={s.sre_resource_group()}",
            f"targetResourceGroups={target_rgs}",
            f"appInsightsId={outputs.get('appInsightsId', '')}",
            f"logAnalyticsId={outputs.get('logAnalyticsId', '')}",
            "permissionLevel=Reader",
            "--query", "properties.outputs", "-o", "json",
        ]

    def _connect_repo(self, azure_result: AzureProvisionResult | None) -> str:
        """Best-effort Phase-2 data-plane repo connect via `az rest`.

        Returns a short skip note when the agent endpoint or a GitHub token is
        unavailable, so a missing data-plane path never fails the whole provision.
        """
        endpoint = azure_result.outputs.get("agentEndpoint", "") if azure_result else ""
        if not endpoint:
            return "deployed; repo connect skipped (no agent endpoint)"
        token = self._run(["gh", "auth", "token"], check=False, capture_output=True, text=True)
        gh_token = getattr(token, "stdout", "").strip()
        if not gh_token:
            return "deployed; repo connect skipped (no GitHub token)"
        # Exact data-plane path: confirm against the SRE Agent API reference
        # (https://learn.microsoft.com/azure/sre-agent/api-reference). az rest acquires
        # the https://azuresre.dev token for us.
        body = json.dumps({"repository": self.spec.github_repo(), "token": gh_token})
        self._run(
            [
                "az", "rest", "--method", "post",
                "--url", f"{endpoint}/repositories",
                "--resource", "https://azuresre.dev",
                "--headers", "Content-Type=application/json",
                "--body", body,
            ],
            check=False,
        )
        return "deployed; repo connected"
```

(Import `render_sre_summary` from `dsf.instance.runtime_render` — renamed in Task 4. Until Task 4 lands, keep the existing `render_sre_onboarding` import name; sequence Task 4 right after.)

- [ ] **Step 5: Run to verify they pass**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k sre -q`
Expected: PASS.

- [ ] **Step 6: Update any test that referenced the old step name**

Run: `grep -rn "onboard_sre_agent" cli` and update remaining references (e.g. step-count or step-name assertions) to `deploy_sre_agent`.

- [ ] **Step 7: Gate + commit**

```bash
uv run ruff check . && uv run lint-imports && uv run pytest -q
git add cli/src/dsf/instance/provisioner.py cli/tests/instance/test_provisioner.py
git commit -m "feat(provision): deploy_sre_agent step (sub deploy + best-effort repo connect)"
```

---

### Task 4: Shrink the runbook renderer to a post-deploy summary

**Files:**
- Modify: `cli/src/dsf/instance/runtime_render.py` (`render_sre_onboarding` / `_render_sre_onboarding_md` near line 129-188; `SreOnboarding` dataclass near line 45; `__all__`)
- Test: `cli/tests/instance/test_runtime_render.py` (the existing SRE onboarding test)

**Interfaces:**
- Produces: `render_sre_summary(manifest, *, repo_root=None) -> SreSummary` writing `sre-agent.md` — a short post-deploy summary (what was created, the agent portal link, the one-time `what-if`/verify note, the `squad:ready` + `incident` label reminder). Keep `HANDOFF_LABEL`/`INCIDENT_LABEL` content.

- [ ] **Step 1: Update the test to the summary shape**

Edit the existing SRE test in `cli/tests/instance/test_runtime_render.py`: call `render_sre_summary`, assert the file is named `sre-agent.md`, contains the agent name (`dsf-sre-<product>`), the monitored RGs, and the `squad:ready`/`incident` reminder; assert it no longer claims onboarding is "interactive (wizard + OAuth)".

- [ ] **Step 2: Run to verify it fails**

Run: `uv run pytest cli/tests/instance/test_runtime_render.py -k sre -q`
Expected: FAIL.

- [ ] **Step 3: Rename + rewrite the renderer**

Rename `SreOnboarding` → `SreSummary` (keep fields `runtime_dir` + rename `onboarding_path` → `summary_path`), `render_sre_onboarding` → `render_sre_summary`, and rewrite `_render_sre_onboarding_md` → `_render_sre_summary_md` to emit a short summary scoped to `spec.product`, `spec.sre_agent_name()`, `spec.monitored_rgs()`, `spec.github_repo()`, dropping the wizard/OAuth steps and keeping the `HANDOFF_LABEL`/`INCIDENT_LABEL` reminder. Update `__all__`.

- [ ] **Step 4: Run to verify it passes**

Run: `uv run pytest cli/tests/instance/test_runtime_render.py -k sre -q`
Expected: PASS.

- [ ] **Step 5: Gate + commit**

```bash
uv run ruff check . && uv run lint-imports && uv run pytest -q
git add cli/src/dsf/instance/runtime_render.py cli/tests/instance/test_runtime_render.py
git commit -m "refactor(provision): SRE runbook becomes a post-deploy summary"
```

---

### Task 5: ADR + docs

**Files:**
- Create: `docs/adr/0015-sre-agent-automated-onboarding.md`
- Modify: `docs/adr/0009-leverage-azure-sre-agent.md` (status → Superseded by 0015)
- Modify: `docs/phases/sre-agent.md` (replace "DSF does not build this... interactive wizard" framing with the automated-provisioning reality)

- [ ] **Step 1: Write ADR 0015**

Cover: ADR 0009's "no headless path" premise is obsolete; DSF now provisions `Microsoft.App/agents` via `infra/sre-agent.bicep` (sub-scoped) with cross-RG read RBAC + subscription Monitoring Contributor + Azure Monitor connectors; Phase-2 repo connect via `az rest` is best-effort; region constraint (Sweden Central EU default); subscription-level RBAC prerequisite (`Owner` or `Contributor` + `User Access Administrator`). Match the house ADR format and the user's voice (casual, no em-dashes, light boldface).

- [ ] **Step 2: Mark ADR 0009 superseded**

Edit the 0009 header: `Status: Superseded by ADR 0015` and a one-line superseded note pointing to 0015.

- [ ] **Step 3: Update the phase doc**

In `docs/phases/sre-agent.md`, replace the "DSF does not build this agent... onboarded interactively per product (wizard plus OAuth)" passages with the automated reality (Bicep provisioning, RBAC on factory + monitored RGs, Azure Monitor connectors, best-effort repo connect), keeping the handoff/feedback sections.

- [ ] **Step 4: Gate + commit**

```bash
uv run ruff check . && uv run lint-imports && uv run pytest -q
git add docs/adr/0015-sre-agent-automated-onboarding.md docs/adr/0009-leverage-azure-sre-agent.md docs/phases/sre-agent.md
git commit -m "docs: ADR 0015 — DSF provisions the SRE agent via Bicep (supersedes 0009)"
```

---

## Self-review

- **Spec coverage:** spec §Components 1 (sre-agent.bicep) → Task 2; 2 (deploy_sre_agent + Phase 2) → Task 3; 3 (InstanceSpec additions) → Task 1; 4 (ADR + summary) → Tasks 4-5. Region/RBAC/error-handling → Tasks 1-3. Testing → each task's offline tests. Covered.
- **Known soft spots (called out, not hidden):** the exact `Microsoft.App/agents` `properties` schema/api-version and the data-plane repo-connect path are read from `microsoft/sre-agent` + the API reference at implementation time. Phase 1 RBAC/connectors are fully specified; Phase 2 repo connect is best-effort with a recorded-skip fallback, so an unstable data-plane path never blocks the agent coming up Azure-Monitor-connected.
- **Out of scope (unchanged from spec):** Phase B (MCP/feedback loop), `Privileged` beyond the param hook, and the `main.bicep` offline-removal stragglers (`DSF_MODE`, Event Grid→Service Bus, seeded `dry_run`).
