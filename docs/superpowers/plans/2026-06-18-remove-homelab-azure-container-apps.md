# Remove the homelab runtime target → Azure Container Apps — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove every "homelab" mention/implementation from the active codebase and retarget the per-product feature-council runtime to **Azure Container Apps (ACA)** with a **user-assigned Managed Identity (UAMI)** for data-plane auth.

**Architecture:** `infra/main.bicep` (deployed by the `provision_azure` step) gains a UAMI, a Container Apps Environment, and one no-ingress orchestrator Container App; the data-plane roles move from the removed `workloadPrincipalId` SP to the UAMI. `render_runtime_bundle` emits an ACA `containerapp.yaml` + resolved `.env.orchestrator` instead of a compose file; `deploy_council` reconciles the app via the injected `az containerapp update` runner under `--execute`. The `homelab-dash` demo product is removed; GRAFANA eval coverage is re-scoped to `microbi`.

**Tech Stack:** Python 3.12 + uv, pydantic models, pytest (injected-runner DI, fully offline), Azure Bicep (ACA), ruff. Build/test: `uv run ruff check .`, `uv run pytest -q`, `uv run python -m dsf.evals.runner --gate`.

**Spec:** `docs/superpowers/specs/2026-06-18-remove-homelab-azure-container-apps-design.md`

---

## File Structure

| File | Responsibility | Action |
| --- | --- | --- |
| `src/dsf/instance/spec.py` | `InstanceSpec` desired state | `runtime_target` default → `aca`; drop `workload_principal_id`; add `runtime_image` |
| `src/dsf/instance/runtime_render.py` | render per-product runtime bundle | compose → ACA `containerapp.yaml`; `RuntimeBundle.compose_path` → `app_config_path` |
| `src/dsf/instance/provisioner.py` | build + apply provisioning plan | `provision_azure` params; `deploy_council` → `az containerapp update`; drop homelab branch |
| `src/dsf/cli/factory.py` | `dsf` CLI | `--runtime-target` default/choices; drop `--workload-principal-id`; docstring |
| `infra/main.bicep` | Azure resources | add UAMI + ACA env + container app; roles → UAMI; drop `workloadPrincipalId`; add `product`/`runtimeImage` |
| `infra/main.parameters.json` | what-if params | drop `workloadPrincipalId` |
| `infra/modules/ingestion.bicep` | Event Grid → Service Bus | drop homelab comments |
| `config/products.json` | demo product registry | delete `homelab-dash` |
| `src/dsf/evals/golden/cases.json` | eval gate cases | re-scope grafana cases to `microbi` |
| `tests/fixtures/grafana_evidence.json` | grafana sample evidence | `homelab-dash` → `microbi`, neutral host |
| `infra/compose.homelab.yml`, `infra/.env.homelab.example` | homelab runtime | **delete** |
| `docs/adr/0004-azure-container-apps-runtime.md` | new ADR | **create** (supersedes 0002) |
| `docs/adr/0002-...md` | old ADR | mark superseded |
| docs (README, RUNBOOK, infra/README, azure.yaml, evals/README, charter, ADR 0001), agent + runtime docstrings | prose | cleanse homelab |

---

## Task 1: Retarget runtime render + deploy to ACA; flip `runtime_target` default

**Files:**
- Modify: `src/dsf/instance/spec.py:35,40`
- Modify: `src/dsf/instance/runtime_render.py` (whole module)
- Modify: `src/dsf/instance/provisioner.py:105,149-171`
- Modify: `src/dsf/cli/factory.py:5,45,49,81-85,101-105`
- Test: `tests/instance/test_runtime_render.py`, `tests/instance/test_provisioner.py`, `tests/instance/test_spec.py`

- [ ] **Step 1: Update the render tests to expect an ACA app config**

In `tests/instance/test_runtime_render.py` replace the three compose-specific tests:

```python
def test_render_writes_app_config_and_env_under_runtime_dir(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    assert bundle.runtime_dir == runtime_dir("microbi", tmp_path)
    assert bundle.runtime_dir == tmp_path / "config" / "instances" / "microbi.runtime"
    assert bundle.app_config_path.is_file()
    assert bundle.env_path.is_file()
    assert bundle.app_config_path.name == "containerapp.yaml"
    assert bundle.env_path.name == ".env.orchestrator"


def test_render_app_config_scopes_product(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    app = bundle.app_config_path.read_text(encoding="utf-8")
    assert "dsf-orchestrator-microbi" in app
    assert "image:" in app
    assert "DSF_PRODUCT" in app
    assert "microbi" in app
```

In `test_render_does_not_inline_secrets`, change the comment referencing the homelab SP to: `# bearer/GitHub tokens are read at runtime from Key Vault via the ACA managed identity (ADR 0004),`.

- [ ] **Step 2: Run the render tests to verify they fail**

Run: `uv run pytest tests/instance/test_runtime_render.py -q`
Expected: FAIL (`RuntimeBundle` has no `app_config_path`).

- [ ] **Step 3: Rewrite `runtime_render.py` to emit `containerapp.yaml`**

Replace the module docstring (lines 1-11) with:

```python
"""Render the per-product feature-council runtime bundle.

For an :class:`~dsf.instance.spec.InstanceManifest`, write a
``containerapp.yaml`` + ``.env.orchestrator`` pair into
``config/instances/<product>.runtime/``. The env file scopes the runtime to the
product (``DSF_PRODUCT``) and carries the Azure backing-service endpoints captured
in the deployment outputs. Secrets (bearer tokens, GitHub tokens) are NOT
rendered — the runtime reads them from Key Vault using its Azure Container Apps
user-assigned managed identity (ADR 0004).
"""
```

Replace `RuntimeBundle` (lines 30-36):

```python
@dataclass(frozen=True)
class RuntimeBundle:
    """Paths to the rendered runtime files for one product."""

    runtime_dir: Path
    app_config_path: Path
    env_path: Path
```

In `_render_env` (lines 44-54) change the secrets comment lines to:

```python
        "# .env.orchestrator — GENERATED by dsf (render_runtime_bundle). Do not edit.",
        "# Endpoints come from the product's Azure deployment outputs. Secrets are",
        "# read at runtime from Key Vault via the ACA managed identity (ADR 0004),",
        "# never rendered here.",
```

Replace `_render_compose` (lines 57-82) with:

```python
def _render_app_config(product: str, resource_group: str, image: str) -> str:
    return (
        "# containerapp.yaml — GENERATED by dsf (render_runtime_bundle).\n"
        f"# App: dsf-orchestrator-{product}\n"
        f"# Feature-council orchestrator runtime for product '{product}', hosted on\n"
        f"# Azure Container Apps in RG '{resource_group}' (ADR 0004). Identity, env, and\n"
        "# scale are provisioned by infra/main.bicep; this is the declarative record\n"
        "# reconciled by `az containerapp update` during deploy_council.\n"
        "properties:\n"
        "  configuration:\n"
        "    activeRevisionsMode: Single\n"
        "  template:\n"
        "    containers:\n"
        "      - name: orchestrator\n"
        f"        image: {image}\n"
        "        env:\n"
        "          - name: DSF_MODE\n"
        "            value: azure\n"
        "          - name: DSF_PRODUCT\n"
        f"            value: {product}\n"
        "    scale:\n"
        "      minReplicas: 1\n"
        "      maxReplicas: 1\n"
    )
```

Replace `render_runtime_bundle` (lines 85-102) body so it writes `containerapp.yaml`:

```python
def render_runtime_bundle(
    manifest: InstanceManifest, *, repo_root: Path | None = None
) -> RuntimeBundle:
    """Render ``containerapp.yaml`` + ``.env.orchestrator`` for ``manifest``.

    Tolerates a manifest with no Azure outputs yet (endpoints render blank).
    """
    product = manifest.spec.product
    outputs = manifest.azure.outputs if manifest.azure else {}
    rdir = runtime_dir(product, repo_root)
    rdir.mkdir(parents=True, exist_ok=True)
    env_path = rdir / ".env.orchestrator"
    app_config_path = rdir / "containerapp.yaml"
    env_path.write_text(_render_env(product, outputs), encoding="utf-8")
    app_config_path.write_text(
        _render_app_config(
            product, manifest.spec.resource_group(), manifest.spec.runtime_image
        ),
        encoding="utf-8",
    )
    return RuntimeBundle(
        runtime_dir=rdir, app_config_path=app_config_path, env_path=env_path
    )
```

- [ ] **Step 4: Add `runtime_image` and flip `runtime_target` default in `spec.py`**

In `src/dsf/instance/spec.py` change line 35 and 40:

```python
    runtime_target: str = "aca"
```

Replace `workload_principal_id: str = ""` (line 40) with:

```python
    runtime_image: str = "ghcr.io/joranbergfeld/dsf-runtime:latest"
```

(`workload_principal_id` is fully removed in Task 2; for now it is replaced by `runtime_image` so the render has an image. Provisioner still references `workload_principal_id` until Task 2 — keep the field temporarily by ALSO leaving a `workload_principal_id: str = ""` line. Concretely, the field block becomes:)

```python
    runtime_target: str = "aca"
    runtime_image: str = "ghcr.io/joranbergfeld/dsf-runtime:latest"
    confidence_threshold: float = 0.6
    name_prefix: str = "dsf"
    environment: str = "dev"
    location: str = "swedencentral"
    workload_principal_id: str = ""
```

- [ ] **Step 5: Update `provisioner.py` `deploy_council` to reconcile the ACA app**

In `src/dsf/instance/provisioner.py` replace the `deploy_council` branch (lines 149-171) with:

```python
                elif step.name == "deploy_council":
                    provisional = InstanceManifest(
                        spec=self.spec, plan=plan, executed=executed, azure=azure_result
                    )
                    render_runtime_bundle(provisional, repo_root=self._repo_root)
                    if not execute:
                        step.result = "rendered (dry-run)"
                    else:
                        self._run(
                            [
                                "az", "containerapp", "update",
                                "--resource-group", self.spec.resource_group(),
                                "--name", f"dsf-orchestrator-{self.spec.product}",
                                "--image", self.spec.runtime_image,
                            ],
                            check=True,
                        )
                        step.executed, step.result = True, "deployed"
```

- [ ] **Step 6: Update the provisioner ACA/dry-run tests**

In `tests/instance/test_provisioner.py`, replace `test_apply_execute_homelab_brings_up_runtime` AND `test_apply_execute_aca_target_raises` (lines 245-277) with a single test:

```python
def test_apply_execute_aca_updates_container_app(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        returncode = 1 if cmd[:3] == ["gh", "repo", "view"] else 0
        return MagicMock(returncode=returncode, stdout="{}")

    spec = InstanceSpec(product="demo", owner="acme")  # runtime_target defaults to aca
    prov = InstanceProvisioner(spec, run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    runtime = tmp_path / "config" / "instances" / "demo.runtime"
    assert (runtime / "containerapp.yaml").is_file()
    assert (runtime / ".env.orchestrator").is_file()
    update = next(c for c in calls if c[:3] == ["az", "containerapp", "update"])
    assert update[update.index("--name") + 1] == "dsf-orchestrator-demo"
    assert "--image" in update
    assert {s.name: s.result for s in manifest.plan.steps}["deploy_council"] == "deployed"
```

- [ ] **Step 7: Flip the CLI default + choices and docstring in `factory.py`**

In `src/dsf/cli/factory.py`:
- Line 3-6 docstring: change `rendered as a homelab compose bundle.` → `deployed to Azure Container Apps.`
- Lines 81-83: `default="aca"`, `choices=["aca"]`.

(Leave `--workload-principal-id` + line 49 for Task 2.)

- [ ] **Step 8: Update `test_spec.py` default-target assertion**

In `tests/instance/test_spec.py`, change the assertion expecting `runtime_target == "homelab"` to `runtime_target == "aca"` (read the file to find the exact line; it is the single homelab reference there).

- [ ] **Step 9: Run the affected suites to green**

Run: `uv run pytest tests/instance/ -q`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/dsf/instance/spec.py src/dsf/instance/runtime_render.py \
  src/dsf/instance/provisioner.py src/dsf/cli/factory.py \
  tests/instance/test_runtime_render.py tests/instance/test_provisioner.py \
  tests/instance/test_spec.py
git commit -m "feat(runtime): render ACA containerapp.yaml + deploy via az containerapp (#28)"
```

---

## Task 2: Managed Identity in Bicep; remove `workloadPrincipalId` end-to-end

**Files:**
- Modify: `infra/main.bicep` (header, params, add UAMI + ACA, role principals, outputs)
- Modify: `infra/main.parameters.json`
- Modify: `infra/modules/ingestion.bicep:4-5,9,24,94` (comments only)
- Modify: `src/dsf/instance/spec.py` (drop `workload_principal_id`)
- Modify: `src/dsf/instance/provisioner.py:102-105` (provision_azure params)
- Modify: `src/dsf/cli/factory.py:49,101-105` (drop arg)
- Test: `tests/instance/test_provisioner.py:48-60`

- [ ] **Step 1: Update `test_plan_provision_azure_command_shape`**

In `tests/instance/test_provisioner.py`, replace `assert "workloadPrincipalId=" in az.command` (line 58) with:

```python
    assert "product=demo" in az.command
    assert any(c.startswith("runtimeImage=") for c in az.command)
```

- [ ] **Step 2: Run it to verify it fails**

Run: `uv run pytest tests/instance/test_provisioner.py::test_plan_provision_azure_command_shape -q`
Expected: FAIL (`workloadPrincipalId=` removed only after Step 3).

- [ ] **Step 3: Update `provision_azure` params in `provisioner.py`**

Replace line 105 (`f"workloadPrincipalId={s.workload_principal_id}",`) with:

```python
                    f"product={s.product}",
                    f"runtimeImage={s.runtime_image}",
```

- [ ] **Step 4: Remove `workload_principal_id` from `spec.py` and the CLI**

In `src/dsf/instance/spec.py` delete the `workload_principal_id: str = ""` line.

In `src/dsf/cli/factory.py`:
- Delete `workload_principal_id=args.workload_principal_id,` (line 49).
- Delete the `--workload-principal-id` argument block (lines 101-105).

- [ ] **Step 5: Run provisioner + CLI tests**

Run: `uv run pytest tests/instance/ tests/cli/ -q`
Expected: PASS.

- [ ] **Step 6: Author the ACA + UAMI Bicep**

In `infra/main.bicep`:

Replace the header comment (lines 1-19) with an ACA-framed version:

```bicep
// main.bicep
// Dark Software Factory — Azure resources for one product factory instance.
//
// Provisions the backing services the intake line depends on (Log Analytics +
// Application Insights, Key Vault, App Configuration with seeded flags, Cosmos DB,
// Event Grid -> Service Bus ingestion) AND the runtime that consumes them: an
// Azure Container Apps environment + a single no-ingress orchestrator Container
// App. The runtime authenticates to the data plane with a user-assigned managed
// identity (ADR 0004); there is no service principal and nothing inbound.
//
// Validate (no deploy):  az deployment group what-if -g <rg> -f infra/main.bicep -p @infra/main.parameters.json
```

Remove the `workloadPrincipalId` param (lines 38-39). Add, after the `environmentName` param (line 36):

```bicep
@description('Product key this factory instance serves (sets DSF_PRODUCT and names the runtime Container App).')
param product string = 'demo'

@description('Container image for the feature-council orchestrator runtime.')
param runtimeImage string = 'ghcr.io/joranbergfeld/dsf-runtime:latest'
```

Add the UAMI immediately after the `appInsights` resource (after line 96):

```bicep
// ---------------------------------------------------------------------------
// Runtime identity: user-assigned MI holding the data-plane roles (ADR 0004).
// A USER-assigned (not system-assigned) identity has a stable principalId known
// before the Container App, avoiding a cycle (the app's env wires in the Cosmos /
// App Config endpoints, while those resources' role assignments need this id).
// ---------------------------------------------------------------------------

resource runtimeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-runtime-${suffix}'
  location: location
  tags: tags
}
```

Change the Key Vault role assignment (lines 127-135) to target the UAMI unconditionally:

```bicep
resource kvSecretsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, runtimeIdentity.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}
```

Change the App Config Data Reader assignment (lines 153-161) the same way:

```bicep
resource appConfigDataReaderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appConfig.id, runtimeIdentity.id, appConfigDataReaderRoleId)
  scope: appConfig
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', appConfigDataReaderRoleId)
  }
}
```

Change the cosmos module param (line 222) `dataPlanePrincipalId: workloadPrincipalId` →
`dataPlanePrincipalId: runtimeIdentity.properties.principalId` (and update the adjacent comment to mention the runtime identity).

Change the ingestion module param (line 239) `receiverPrincipalId: workloadPrincipalId` →
`receiverPrincipalId: runtimeIdentity.properties.principalId` (and update the adjacent comment).

Add the ACA environment + app after the `ingestion` module (after line 242):

```bicep
// ---------------------------------------------------------------------------
// Runtime compute: Azure Container Apps environment + orchestrator app (ADR 0004)
// ---------------------------------------------------------------------------

resource containerEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-cae-${suffix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

resource orchestratorApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'dsf-orchestrator-${product}'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
    }
    template: {
      containers: [
        {
          name: 'orchestrator'
          image: runtimeImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'DSF_MODE', value: 'azure' }
            { name: 'DSF_PRODUCT', value: product }
            { name: 'AZURE_CLIENT_ID', value: runtimeIdentity.properties.clientId }
            { name: 'AZURE_APPCONFIG_ENDPOINT', value: appConfig.properties.endpoint }
            { name: 'AZURE_KEYVAULT_URI', value: keyVault.properties.vaultUri }
            { name: 'AZURE_COSMOS_ENDPOINT', value: cosmos.outputs.endpoint }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsights.properties.ConnectionString
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}
```

Update the outputs section comment (line 245) from `consumed by the homelab runtime's azure-mode configuration` → `consumed by the ACA runtime's azure-mode configuration`. Add two outputs at the end:

```bicep
@description('Principal ID of the runtime user-assigned identity (data-plane RBAC holder).')
output runtimePrincipalId string = runtimeIdentity.properties.principalId

@description('Name of the orchestrator Container App.')
output orchestratorAppName string = orchestratorApp.name
```

- [ ] **Step 7: Remove `workloadPrincipalId` from `main.parameters.json`**

Delete the `"workloadPrincipalId": { "value": "" },` block so the file lists only `namePrefix`, `environmentName`, `adminPrincipalId`.

- [ ] **Step 8: Cleanse `ingestion.bicep` homelab comments**

In `infra/modules/ingestion.bicep` rewrite the homelab comment lines (4-5, 9, 24, 94) to describe the ACA runtime polling the queue (no homelab/tunnel framing), e.g. line 24 description → `'Object ID of the runtime identity granted Service Bus Data Receiver. Empty = skip.'`.

- [ ] **Step 9: Validate Bicep compiles (offline) if `bicep` is available**

Run: `az bicep build -f infra/main.bicep --stdout >/dev/null && echo OK` (skip if `az`/`bicep` not installed — no Azure calls are made either way).
Expected: `OK` (no compile errors), or skipped.

- [ ] **Step 10: Run instance tests + ruff**

Run: `uv run pytest tests/instance/ -q && uv run ruff check src/dsf/instance src/dsf/cli`
Expected: PASS + clean.

- [ ] **Step 11: Commit**

```bash
git add infra/main.bicep infra/main.parameters.json infra/modules/ingestion.bicep \
  src/dsf/instance/spec.py src/dsf/instance/provisioner.py src/dsf/cli/factory.py \
  tests/instance/test_provisioner.py
git commit -m "feat(infra): ACA env + container app on a user-assigned MI; drop workloadPrincipalId (#28)"
```

---

## Task 3: Remove the `homelab-dash` demo product; re-scope GRAFANA coverage to `microbi`

**Files:**
- Modify: `config/products.json` (delete `homelab-dash` object)
- Modify: `src/dsf/evals/golden/cases.json` (re-scope grafana cases)
- Modify: `tests/fixtures/grafana_evidence.json` (`homelab-dash` → `microbi`, neutral host)
- Modify: `tests/config/test_registry.py`, `tests/config/test_flags.py`, `tests/evals/test_runner.py`, `tests/agents/grafana/test_grafana.py`, `tests/agents/grafana/test_grafana_live.py`

- [ ] **Step 1: Delete the `homelab-dash` product from `config/products.json`**

Remove the entire second product object (lines 16-28), leaving `microbi` as the sole product. The `products` array now has one element.

- [ ] **Step 2: Re-scope the grafana golden cases in `cases.json`**

In `src/dsf/evals/golden/cases.json`:
- Case `grafana-homelab-latency` (lines 20-37): rename `id` → `grafana-microbi-latency`, `name` → `"Grafana latency/memory signals route to microbi and file"`, `product_hints` → `["microbi"]`, `text` → `"Checkout API p99 latency spike and node memory saturation in microbi"`, `expected_product` → `"microbi"`.
- Case `sentry-grafana-multi-source` (lines 56-75): `product_hints` → `["microbi"]`, `text` → `"Multi-source incident: microbi errors plus latency saturation"`. (`expected_product` stays `null`.)

- [ ] **Step 3: Re-scope the grafana evidence fixture**

In `tests/fixtures/grafana_evidence.json`, for all three entries: change every `product_hints` value `"homelab-dash"` → `"microbi"`, and change citation hosts `grafana.homelab.lan` → `grafana.example.com`.

- [ ] **Step 4: Update the affected tests to drop homelab-dash**

Read each test, then replace `homelab-dash`/`homelab-overview` expectations with `microbi`/`microbi-overview` (or remove homelab-only assertions):
- `tests/config/test_registry.py` (9 refs)
- `tests/config/test_flags.py` (1 ref)
- `tests/evals/test_runner.py` (2 refs)
- `tests/agents/grafana/test_grafana.py` (7 refs)
- `tests/agents/grafana/test_grafana_live.py` (7 refs)

- [ ] **Step 5: Run the affected suites + the eval gate**

Run: `uv run pytest tests/config tests/evals tests/agents/grafana -q && uv run python -m dsf.evals.runner --gate`
Expected: PASS + gate PASSED.

- [ ] **Step 6: Commit**

```bash
git add config/products.json src/dsf/evals/golden/cases.json tests/fixtures/grafana_evidence.json \
  tests/config/test_registry.py tests/config/test_flags.py tests/evals/test_runner.py \
  tests/agents/grafana/test_grafana.py tests/agents/grafana/test_grafana_live.py
git commit -m "chore(demo): remove homelab-dash product; re-scope grafana coverage to microbi (#28)"
```

---

## Task 4: Delete homelab files; add ADR 0004; cleanse docs + comments

**Files:**
- Delete: `infra/compose.homelab.yml`, `infra/.env.homelab.example`
- Create: `docs/adr/0004-azure-container-apps-runtime.md`
- Modify: `docs/adr/0002-homelab-runtime-azure-backing-only.md` (mark superseded)
- Modify: `docs/adr/0001-architecture-decisions.md`, `README.md`, `docs/RUNBOOK.md`, `infra/README.md`, `infra/azure.yaml`, `src/dsf/evals/README.md`, `.env.example`, the charter `…template-charter-design.md` §8
- Modify: `src/dsf/agents/grafana/{backend.py:7,main.py:7,__init__.py:7}`, `src/dsf/agents/sentry/mcp_client.py:4`, `src/dsf/runtime/__init__.py:3-5`

- [ ] **Step 1: Delete the homelab infra files**

```bash
git rm infra/compose.homelab.yml infra/.env.homelab.example
```

- [ ] **Step 2: Create ADR 0004**

Create `docs/adr/0004-azure-container-apps-runtime.md`:

```markdown
# ADR 0004: Azure Container Apps runtime with a user-assigned managed identity

- Status: Accepted
- Date: 2026-06-18
- Supersedes: ADR 0002 (homelab runtime, Azure backing services only)

## Context

ADR 0002 hosted the factory runtime (orchestrator + source agents) in a homelab
behind NAT, reaching Azure backing services outbound only, authenticating with an
Entra service principal (`workloadPrincipalId`) because managed identity only works
inside Azure. The owner has retired the homelab: the runtime now runs in Azure.

## Decision

- Host the per-product feature-council orchestrator on **Azure Container Apps** in
  the product's resource group, alongside the backing services it consumes.
- Provision a **user-assigned managed identity** that holds the data-plane roles
  (Key Vault Secrets User, App Configuration Data Reader, Cosmos data-plane, Service
  Bus Data Receiver). A user-assigned identity (not system-assigned) has a stable
  principalId known before the Container App, avoiding a dependency cycle between the
  app's env (which wires in the Cosmos/App Config endpoints) and those resources'
  role assignments.
- `infra/main.bicep` provisions the Container Apps environment + orchestrator app
  (no ingress — it is a worker) with the identity attached and env wired from the
  sibling resources. `provision_azure` deploys it; `deploy_council` reconciles the
  app image via `az containerapp update`.
- Remove the homelab compose bundle, the tunnel sidecar, and the `workloadPrincipalId`
  service principal entirely.

## Consequences

- No inbound exposure; the orchestrator and agents are co-located in Azure and reach
  data sources over their existing authenticated endpoints.
- `DefaultAzureCredential` selects the user-assigned identity via `AZURE_CLIENT_ID`.
- ADR 0002 is superseded and kept for history.
```

- [ ] **Step 3: Mark ADR 0002 superseded**

In `docs/adr/0002-homelab-runtime-azure-backing-only.md`, change the Status line to `Status: Superseded by ADR 0004` and add a one-line banner under the title noting the runtime moved to Azure Container Apps. Leave the rest as a historical record.

- [ ] **Step 4: Cleanse the prose docs**

Edit each of `docs/adr/0001-architecture-decisions.md`, `README.md`, `docs/RUNBOOK.md`, `infra/README.md`, `infra/azure.yaml`, `src/dsf/evals/README.md`, `.env.example`, and the charter design doc §8 to remove homelab framing and describe the ACA runtime + UAMI auth instead. (Read each file's homelab lines and rewrite them; do not leave the word "homelab".)

- [ ] **Step 5: Cleanse source docstrings/comments**

- `src/dsf/runtime/__init__.py` (lines 3-5): replace the homelab wording with `runs as an Azure Container App in the product's resource group`.
- `src/dsf/agents/grafana/backend.py:7`, `main.py:7`, `__init__.py:7`: replace "runs inside a homelab behind NAT" with "runs in Azure Container Apps".
- `src/dsf/agents/sentry/mcp_client.py:4`: replace the homelab example with a neutral one (e.g. `a Sentry MCP server reachable over Streamable HTTP`).

- [ ] **Step 6: Run ruff + full suite**

Run: `uv run ruff check . && uv run pytest -q`
Expected: clean + green.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "docs: ADR 0004 (ACA runtime) supersedes 0002; remove homelab files + framing (#28)"
```

---

## Task 5: Verification — homelab is gone

**Files:** none (verification only)

- [ ] **Step 1: Assert zero homelab references outside dated records**

Run:

```bash
grep -rni homelab . \
  --include="*.py" --include="*.bicep" --include="*.json" --include="*.yml" \
  --include="*.yaml" --include="*.md" --include="*.example" --include="*.toml" \
  --include="Dockerfile" 2>/dev/null \
  | grep -v "/.git/" \
  | grep -v "docs/superpowers/specs/2026-06-1[5-8]" \
  | grep -v "docs/superpowers/plans/2026-06-1"
```

Expected: **no output** (exit 1). The only allowed homelab references are the dated, historical specs/plans records.

- [ ] **Step 2: Full gate**

Run: `uv run ruff check . && uv run pytest -q && uv run python -m dsf.evals.runner --gate`
Expected: clean, all tests green, gate PASSED.

- [ ] **Step 3: Final review + mark workstream done**

Confirm the spec Done-when list is satisfied; update the session todo `remove-homelab` → done; proceed to the fakes-out workstream (#27).

---

## Self-Review

- **Spec coverage:** Goal 1 (remove all homelab) → Tasks 1-5; Goal 2 (ACA host) → Tasks 1-2; Goal 3 (UAMI) → Task 2; Goal 4 (ADR 0004) → Task 4. Decision A (ACA here) → Tasks 1-2; B (UAMI) → Task 2; C (full retarget) → Tasks 1-2; D (agents in ACA, tunnel gone) → Tasks 2,4; E (no new fakes) → all tests use the injected runner; F (remove homelab-dash, re-scope to microbi) → Task 3.
- **Placeholders:** doc/comment edits in Task 4 Steps 4-5 and the test edits in Task 3 Step 4 are enumerated by file with the exact transformation (homelab → ACA/microbi); execute by reading each file's homelab lines then rewriting. No code step is left without code.
- **Type consistency:** `RuntimeBundle.app_config_path` (Task 1) is referenced consistently in tests (Task 1 Step 1) and provisioner renders but addresses the app by name `dsf-orchestrator-<product>` (Tasks 1,2). `runtime_image` added in Task 1 Step 4 is consumed by render (Task 1 Step 3) and provision_azure + deploy_council (Tasks 1-2). `product`/`runtimeImage` Bicep params (Task 2) match the `provision_azure` `product=`/`runtimeImage=` args (Task 2 Step 3).
