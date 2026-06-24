# Stage 1: Retire the AKS/Ralph Squad Harness — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete the per-product AKS + KEDA + Ralph "coding squad" provisioning harness so the creation phase is ready to be rebuilt on the GitHub Copilot Coding Agent (later stages), leaving the test/lint/import suite fully green.

**Architecture:** This is a pure subtraction. We remove the dead Kubernetes manifest renderer, the KEDA issue-count exporter helper, the `squad_init` and `deploy_squad_ralph` provisioning steps and their apply branches, the runtime GitHub-token Key Vault seed, and the AKS/squad Bicep resources + outputs. We keep everything that later stages or the ADR 0007 handoff contract still need.

**Tech Stack:** Python 3.12, `uv` workspace, pytest (`--import-mode=importlib`, `asyncio_mode=auto`), ruff (`check` only — line-length 100, rules `E,F,I,UP,B`; `ruff format` is NOT used), import-linter (`lint-imports`), Azure Bicep (validated with `az bicep build`).

**Spec:** `docs/superpowers/specs/2026-06-22-creation-phase-coding-agent-reflection-design.md`
**ADR:** `docs/adr/0016-creation-phase-coding-agent-reflection.md` (supersedes ADR 0012)

---

## What is KEPT (do NOT delete — later stages / ADR 0007 depend on these)

- `core/src/dsf/contracts/handoff.py` — `HANDOFF_LABEL` (`squad:ready`), `INCIDENT_LABEL`, colors, descriptions. The label taxonomy is the council↔creation contract (ADR 0007). Untouched.
- `cli/src/dsf/instance/squad_governance.py` + its `squad_governance` provisioning step + `test_squad_governance_low_maturity_disables_auto_merge`. Governance is `gh api`-based (no `squad` CLI, no AKS). Stage 3 territory.
- `InstanceSpec.squad_maturity` in `cli/src/dsf/instance/spec.py` and `cli/src/dsf/cli/factory.py`. Renamed to `creation_maturity` in **Stage 3**, not now.
- `keyVaultName` Bicep output and `keyVaultAdminAssignment` (Secrets Officer) role — Stage 2's GitHub App private-key secret needs them. Only the *wording* of their comments changes here.
- `create_repo` step's `--clone` flag and the clone-fallback branch in `apply()`. Keep behavior; only reword comments that mention "squad".
- `_seed_appconfig` and the `_SEED_MAX_ATTEMPTS` / `_SEED_RETRY_DELAY` retry helper (these belong to App Configuration seeding, **not** the squad token seed).

## File Structure (this stage)

**Delete:**
- `cli/src/dsf/instance/squad_render.py` (Ralph/KEDA/identity/exporter manifest renderer; defines `KV_SECRET_NAME`, `render_squad_bundle`)
- `cli/tests/instance/test_squad_render.py`
- `cli/src/dsf/instance/issue_count.py` (KEDA scale-metric helper `open_handoff_issue_count`; only consumer was the deleted exporter)
- `cli/tests/instance/test_issue_count.py`
- `infra/modules/aks.bicep`

**Modify:**
- `cli/src/dsf/instance/provisioner.py` — remove `squad_render` import, `squad_init` + `deploy_squad_ralph` plan steps, `deploy_squad_ralph` apply branch, `_seed_github_token`, the now-orphaned `repo_dir` local, reword two comments + the module docstring.
- `cli/tests/instance/test_provisioner.py` — drop the two removed steps from the order assertion, delete three squad tests, extend the "removed steps" guard test, remove the now-unused `from pathlib import Path` import, reword two comments.
- `infra/main.bicep` — remove the `aks` module, `squadIdentity`, `squadFederation`, `squadKvSecretsUser`, and the `aksName` / `squadIdentityClientId` / `tenantId` outputs; reword two comments.
- `cli/src/dsf/instance/runtime_render.py` — reword the SRE summary's handoff-label sentence (drop "coding squad's Ralph watch loop"); keep the `{HANDOFF_LABEL}` token.

---

## Task 1: Retire the Ralph/KEDA deploy path

Removes `deploy_squad_ralph` (plan step + apply branch), the runtime GitHub-token Key Vault seed, and the two now-dead helper modules.

**Files:**
- Modify: `cli/tests/instance/test_provisioner.py`
- Modify: `cli/src/dsf/instance/provisioner.py`
- Delete: `cli/src/dsf/instance/squad_render.py`, `cli/tests/instance/test_squad_render.py`
- Delete: `cli/src/dsf/instance/issue_count.py`, `cli/tests/instance/test_issue_count.py`

- [ ] **Step 1: Update the tests to the post-Ralph reality**

In `cli/tests/instance/test_provisioner.py`:

(1a) Remove `"deploy_squad_ralph",` from the step-order list in `test_plan_step_order_and_names`:

```python
        "register_product",
        "deploy_council",
        "deploy_squad_ralph",
        "squad_governance",
```

becomes:

```python
        "register_product",
        "deploy_council",
        "squad_governance",
```

(1b) Extend the removed-steps guard test to cover `deploy_squad_ralph`:

```python
def test_removed_one_shot_squad_steps_are_gone():
    """The pre-Ralph Cloud Agent steps (squad_copilot, squad_triage) are gone."""
    plan = InstanceProvisioner(_spec()).plan()
    names = {s.name for s in plan.steps}
    assert not names & {"squad_copilot", "squad_triage"}
```

becomes:

```python
def test_removed_one_shot_squad_steps_are_gone():
    """The retired squad steps (Cloud Agent + AKS/Ralph harness) are gone."""
    plan = InstanceProvisioner(_spec()).plan()
    names = {s.name for s in plan.steps}
    assert not names & {"squad_copilot", "squad_triage", "deploy_squad_ralph"}
```

(1c) Delete both Ralph apply tests in full — `test_deploy_squad_ralph_renders_bundle_in_dry_run` and `test_deploy_squad_ralph_applies_manifests_on_execute` (the entire block from `def test_deploy_squad_ralph_renders_bundle_in_dry_run(tmp_path):` through the line `assert write_config.result.endswith("demo.json")` and its preceding comment, i.e. everything up to but not including `def test_plan_create_labels_includes_incident_marker():`).

(1d) Remove the now-unused import (its only use, `Path(cmd[-1]).name`, lived in the deleted Ralph test):

```python
import json
import subprocess
from pathlib import Path
from unittest.mock import MagicMock
```

becomes:

```python
import json
import subprocess
from unittest.mock import MagicMock
```

- [ ] **Step 2: Run the test file to confirm it now fails against the un-edited provisioner**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -q`
Expected: FAIL — `test_plan_step_order_and_names` reports the actual plan still contains `"deploy_squad_ralph"` while the expected list no longer does.

- [ ] **Step 3: Remove the `squad_render` import from the provisioner**

In `cli/src/dsf/instance/provisioner.py`, delete this line (line ~44):

```python
from dsf.instance.squad_governance import governance_commands
from dsf.instance.squad_render import KV_SECRET_NAME, render_squad_bundle
```

becomes:

```python
from dsf.instance.squad_governance import governance_commands
```

- [ ] **Step 4: Remove the `deploy_squad_ralph` plan step**

In `provisioner.py` `plan()`, delete the whole `ProvisionStep(name="deploy_squad_ralph", ...)` block:

```python
            ProvisionStep(
                name="deploy_council",
                description=(
                    f"Render + bring up the feature-council runtime scoped to {s.product}"
                ),
            ),
            ProvisionStep(
                name="deploy_squad_ralph",
                description=(
                    f"Render + apply the Ralph watch loop (AKS + KEDA) for {s.product}"
                ),
            ),
            ProvisionStep(
                name="squad_governance",
```

becomes:

```python
            ProvisionStep(
                name="deploy_council",
                description=(
                    f"Render + bring up the feature-council runtime scoped to {s.product}"
                ),
            ),
            ProvisionStep(
                name="squad_governance",
```

- [ ] **Step 5: Remove the `deploy_squad_ralph` apply branch**

In `provisioner.py` `apply()`, delete the entire `elif step.name == "deploy_squad_ralph":` branch:

```python
                step.executed, step.result = True, "deployed"
        elif step.name == "deploy_squad_ralph":
            provisional = InstanceManifest(
                spec=self.spec, plan=plan, executed=executed, azure=azure_result
            )
            bundle = render_squad_bundle(provisional, repo_root=self._repo_root)
            if not execute:
                step.result = "rendered (dry-run)"
            else:
                self._run(
                    [
                        "az", "aks", "get-credentials",
                        "--resource-group", self.spec.resource_group(),
                        "--name", f"aks-dsf-{self.spec.product}",
                        "--overwrite-existing",
                    ],
                    check=True,
                )
                self._seed_github_token(azure_result)
                for manifest_file in (
                    bundle.identity_path,
                    bundle.exporter_path,
                    bundle.deployment_path,
                    bundle.scaledobject_path,
                ):
                    self._run(
                        ["kubectl", "apply", "-f", str(manifest_file)],
                        check=True,
                    )
                step.executed, step.result = True, "deployed"
        elif step.name == "deploy_sre_agent":
```

becomes:

```python
                step.executed, step.result = True, "deployed"
        elif step.name == "deploy_sre_agent":
```

- [ ] **Step 6: Remove the `_seed_github_token` method**

In `provisioner.py`, delete the whole method (it is only called from the branch removed in Step 5; do NOT touch `_seed_appconfig` directly above it):

```python
        assert last_error is not None  # loop ran at least once
        raise last_error

    def _seed_github_token(self, azure_result: AzureProvisionResult | None) -> None:
        """Seed the operator's GitHub token into the per-product Key Vault.

        The squad pods never hold a static in-cluster credential; the Key Vault
        CSI driver projects this secret at runtime under AKS workload identity
        (Option 2, ADR 0012). Swapping to a GitHub App installation token later is
        a value change here, not a rewrite. Reads the vault name from the Azure
        deployment outputs captured by the ``provision_azure`` step.
        """
        keyvault_name = azure_result.outputs.get("keyVaultName", "") if azure_result else ""
        token = self._run(
            ["gh", "auth", "token"], check=True, capture_output=True, text=True
        )
        self._run(
            [
                "az", "keyvault", "secret", "set",
                "--vault-name", keyvault_name,
                "--name", KV_SECRET_NAME,
                "--value", getattr(token, "stdout", "").strip(),
            ],
            check=True,
        )
```

becomes:

```python
        assert last_error is not None  # loop ran at least once
        raise last_error
```

- [ ] **Step 7: Delete the four dead files**

Run:

```bash
git rm cli/src/dsf/instance/squad_render.py cli/tests/instance/test_squad_render.py \
       cli/src/dsf/instance/issue_count.py cli/tests/instance/test_issue_count.py
```

- [ ] **Step 8: Confirm no dangling references to the deleted symbols remain**

Run: `grep -rn "squad_render\|render_squad_bundle\|KV_SECRET_NAME\|issue_count\|open_handoff_issue_count\|_seed_github_token" cli/`
Expected: no matches (empty output).

- [ ] **Step 9: Run the CLI instance tests and lint**

Run: `uv run pytest cli/tests/instance -q`
Expected: PASS (no failures, no errors).

Run: `uv run ruff check cli/`
Expected: `All checks passed!` (confirms the removed `Path` import left nothing unused).

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "refactor(instance): retire Ralph/KEDA squad deploy path

Remove deploy_squad_ralph (plan step + apply branch), the runtime
GitHub-token Key Vault seed, and the now-dead squad_render / issue_count
helpers. Part of the ADR 0016 creation-phase redesign (#71); the Copilot
Coding Agent replaces the AKS-hosted Ralph harness.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 2: Remove the `squad_init` bootstrap step

Drops the `squad init` provisioning step (the local `squad` CLI is no longer used) and the now-orphaned `repo_dir` local.

**Files:**
- Modify: `cli/tests/instance/test_provisioner.py`
- Modify: `cli/src/dsf/instance/provisioner.py`

- [ ] **Step 1: Update the tests**

In `cli/tests/instance/test_provisioner.py`:

(1a) Remove `"squad_init",` from the step-order list in `test_plan_step_order_and_names`:

```python
        "create_repo",
        "create_labels",
        "squad_init",
        "create_resource_group",
```

becomes:

```python
        "create_repo",
        "create_labels",
        "create_resource_group",
```

(1b) Delete `test_plan_squad_steps_run_in_repo_dir` in full:

```python
def test_plan_squad_steps_run_in_repo_dir():
    plan = InstanceProvisioner(_spec()).plan()
    for name in ("squad_init",):
        step = next(s for s in plan.steps if s.name == name)
        assert step.cwd == "demo"
        assert step.command[0] == "squad"


```

(1c) In the execute test, drop the `squad_init` assertions and reword the comment:

```python
    executed = [cmd for cmd, _ in calls]
    # repo created and squad initialized in the cloned repo dir:
    assert ["gh", "repo", "create", "acme/demo", "--private", "--clone"] in executed
    squad_init = next((cmd, cwd) for cmd, cwd in calls if cmd[:2] == ["squad", "init"])
    assert squad_init[1] == "demo"
    # azure now provisions for real (RG + Bicep deployment):
```

becomes:

```python
    executed = [cmd for cmd, _ in calls]
    # repo created (and cloned locally) for the product:
    assert ["gh", "repo", "create", "acme/demo", "--private", "--clone"] in executed
    # azure now provisions for real (RG + Bicep deployment):
```

(1d) Reword the clone-fallback test comment so it no longer references squad steps:

```python
    # repo exists remotely but isn't cloned here -> clone so squad steps have a cwd:
    assert ["gh", "repo", "create", "acme/demo", "--private", "--clone"] not in calls
```

becomes:

```python
    # repo exists remotely but isn't cloned here -> clone so we have a local checkout:
    assert ["gh", "repo", "create", "acme/demo", "--private", "--clone"] not in calls
```

(1e) Extend the removed-steps guard (updated in Task 1) to also cover `squad_init`:

```python
    assert not names & {"squad_copilot", "squad_triage", "deploy_squad_ralph"}
```

becomes:

```python
    assert not names & {"squad_copilot", "squad_triage", "deploy_squad_ralph", "squad_init"}
```

- [ ] **Step 2: Run the test file to confirm it fails against the un-edited provisioner**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -q`
Expected: FAIL — `test_plan_step_order_and_names` reports the actual plan still contains `"squad_init"`.

- [ ] **Step 3: Remove the `squad_init` plan step**

In `provisioner.py` `plan()`, delete the `ProvisionStep(name="squad_init", ...)` block:

```python
            ProvisionStep(
                name="create_labels",
                description=(
                    f"Create the label taxonomy + handoff label in {s.github_repo()}"
                ),
                commands=_label_commands(s),
            ),
            ProvisionStep(
                name="squad_init",
                description=f"Initialize Coding Squad in {s.github_repo()}",
                command=["squad", "init", "--preset", "default"],
                cwd=repo_dir,
            ),
            ProvisionStep(
                name="create_resource_group",
```

becomes:

```python
            ProvisionStep(
                name="create_labels",
                description=(
                    f"Create the label taxonomy + handoff label in {s.github_repo()}"
                ),
                commands=_label_commands(s),
            ),
            ProvisionStep(
                name="create_resource_group",
```

- [ ] **Step 4: Remove the now-orphaned `repo_dir` local**

`repo_dir` was only consumed by the `squad_init` step's `cwd`. In `plan()`:

```python
    def plan(self) -> InstancePlan:
        """Return the ordered provisioning plan (pure — no side effects)."""
        s = self.spec
        repo_dir = s.resolved_repo()
        bicep = str((self._repo_root or _default_repo_root()) / "infra" / "main.bicep")
```

becomes:

```python
    def plan(self) -> InstancePlan:
        """Return the ordered provisioning plan (pure — no side effects)."""
        s = self.spec
        bicep = str((self._repo_root or _default_repo_root()) / "infra" / "main.bicep")
```

(Note: the separate `repo_dir = self.spec.resolved_repo()` inside `apply()`'s `create_repo` branch is unrelated — leave it.)

- [ ] **Step 5: Reword the module docstring (drop the `squad` CLI)**

```python
"""InstanceProvisioner — build and apply a provisioning plan for an instance.

External CLIs (``gh``, ``squad``, ``az``) are invoked through an injectable
``run`` callable (defaults to :func:`subprocess.run`) so tests stay offline,
mirroring :class:`dsf.github_client.RealGitHubClient`.
"""
```

becomes:

```python
"""InstanceProvisioner — build and apply a provisioning plan for an instance.

External CLIs (``gh``, ``az``) are invoked through an injectable
``run`` callable (defaults to :func:`subprocess.run`) so tests stay offline,
mirroring :class:`dsf.github_client.RealGitHubClient`.
"""
```

- [ ] **Step 6: Reword the clone-fallback comment in `apply()`**

```python
            else:
                # Repo exists remotely but isn't cloned here; the squad steps
                # need a local working copy, so clone it.
                self._run(
```

becomes:

```python
            else:
                # Repo exists remotely but isn't cloned here; later steps need a
                # local working copy, so clone it.
                self._run(
```

- [ ] **Step 7: Run the CLI instance tests and lint**

Run: `uv run pytest cli/tests/instance -q`
Expected: PASS (no failures).

Run: `uv run ruff check cli/`
Expected: `All checks passed!` (confirms `repo_dir` is not flagged as unused — because it is gone).

- [ ] **Step 8: Confirm the `squad` CLI is no longer invoked anywhere in the provisioner**

Run: `grep -rn '"squad"' cli/src/dsf/instance/provisioner.py`
Expected: no matches (squad_governance shells `gh`, not `squad`).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "refactor(instance): drop squad_init bootstrap step

The local 'squad' CLI is no longer part of provisioning; remove the
squad_init step and its orphaned repo_dir local. Part of the ADR 0016
creation-phase redesign (#71).

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 3: Strip the squad/AKS resources from Bicep

Removes the AKS cluster module, the squad workload identity + federation + Key Vault role assignment, and the three squad-only deployment outputs. Keeps `keyVaultName` and `keyVaultAdminAssignment` (Stage 2 needs them).

**Files:**
- Delete: `infra/modules/aks.bicep`
- Modify: `infra/main.bicep`

- [ ] **Step 1: Delete the AKS module**

Run: `git rm infra/modules/aks.bicep`

- [ ] **Step 2: Remove the squad/AKS resource block from `infra/main.bicep`**

Delete the entire block from the `// Coding squad compute` comment header through `squadKvSecretsUser` (everything between the OpenAI role assignment's closing `}` and the `// Runtime compute` section header):

```bicep
// ---------------------------------------------------------------------------
// Coding squad compute: per-product AKS cluster running the Ralph watch loop,
// scaled 0..1 by KEDA off the open squad:ready issue count (ADR 0012)
// ---------------------------------------------------------------------------

module aks 'modules/aks.bicep' = {
  name: 'aks'
  params: {
    namePrefix: namePrefix
    location: location
    product: product
  }
}

// Squad workload identity: a dedicated user-assigned identity federated to the
// squad-<product> Kubernetes service account, granted Key Vault Secrets User so
// the CSI driver can project the GitHub token secret into the Ralph + exporter
// pods (ADR 0012).
resource squadIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-squad-${suffix}'
  location: location
  tags: tags
}

resource squadFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: squadIdentity
  name: 'squad-${product}'
  properties: {
    issuer: aks.outputs.aksOidcIssuerUrl
    subject: 'system:serviceaccount:squad-${product}:squad-${product}'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

resource squadKvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, squadIdentity.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    principalId: squadIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}

// ---------------------------------------------------------------------------
// Runtime compute: Azure Container Apps environment + orchestrator app (ADR 0004)
```

becomes:

```bicep
// ---------------------------------------------------------------------------
// Runtime compute: Azure Container Apps environment + orchestrator app (ADR 0004)
```

- [ ] **Step 3: Remove the three squad-only outputs**

Delete the `aksName`, `squadIdentityClientId`, and `tenantId` outputs, and reword the `keyVaultName` output's description (it currently references the squad SecretProviderClass):

```bicep
@description('Name of the orchestrator Container App.')
output orchestratorAppName string = orchestratorApp.name

output aksName string = aks.outputs.aksName

@description('Client ID of the squad workload identity (for the K8s ServiceAccount annotation).')
output squadIdentityClientId string = squadIdentity.properties.clientId

@description('Key Vault name (for the squad SecretProviderClass).')
output keyVaultName string = keyVault.name

@description('Entra tenant ID (for the squad SecretProviderClass).')
output tenantId string = subscription().tenantId
```

becomes:

```bicep
@description('Name of the orchestrator Container App.')
output orchestratorAppName string = orchestratorApp.name

@description('Name of the per-product Key Vault.')
output keyVaultName string = keyVault.name
```

- [ ] **Step 4: Reword the Key Vault admin-grant comment**

The Secrets Officer grant stays (Stage 2's GitHub App private key needs it); only its rationale comment changes:

```bicep
// Admin (human/operator) gets Secrets Officer so `dsf new` can seed the
// squad GitHub token into the vault (ADR 0012), mirroring the App Config admin grant.
// Data-plane reachability still requires allowPublicNetworkAccess=true (or running
// provisioning from inside the vault's network); see docs/site/get-started/operate.md.
```

becomes:

```bicep
// Admin (human/operator) gets Secrets Officer so `dsf new` can seed product
// secrets into the vault, mirroring the App Config admin grant.
// Data-plane reachability still requires allowPublicNetworkAccess=true (or running
// provisioning from inside the vault's network); see docs/site/get-started/operate.md.
```

- [ ] **Step 5: Confirm no squad/AKS references remain in `infra/main.bicep`**

Run: `grep -niE "squad|aks|ralph|keda" infra/main.bicep`
Expected: no matches (empty output).

- [ ] **Step 6: Validate the Bicep still compiles**

Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo BICEP_OK`
Expected: `BICEP_OK` with no errors (a clean compile proves there are no dangling references to the removed `aks` module, `squadIdentity`, `keyVaultSecretsUserRoleId` usage, etc.).

> If `az` is unavailable in the execution environment, instead run `grep -niE "aks|squad|squadIdentity" infra/main.bicep` and confirm zero matches, then rely on the provisioner tests in Step 7.

- [ ] **Step 7: Confirm the provisioner tests still pass (the Bicep path is unchanged)**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -q`
Expected: PASS — `test_plan_provision_azure_command_shape` still finds `infra/main.bicep`.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "infra: remove AKS cluster + squad workload identity from Bicep

Delete modules/aks.bicep and the squad identity/federation/role-assignment
resources and their outputs (aksName, squadIdentityClientId, tenantId).
Keep keyVaultName + the Secrets Officer admin grant for the Stage 2 GitHub
App key. Part of the ADR 0016 creation-phase redesign (#71).

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 4: Reword the SRE runtime summary

The SRE onboarding summary tells operators what the handoff label does. Drop the "coding squad's Ralph watch loop" phrasing but keep the `{HANDOFF_LABEL}` token (it renders as `squad:ready`, which `test_runtime_render.py` asserts is present).

**Files:**
- Modify: `cli/src/dsf/instance/runtime_render.py`
- Test: `cli/tests/instance/test_runtime_render.py` (no edit — must keep passing)

- [ ] **Step 1: Reword the handoff-label sentence**

In `runtime_render.py`:

```python
        "## Labels (keep these)\n\n"
        f"The `{HANDOFF_LABEL}` label routes SRE-filed issues to the coding squad's\n"
        "Ralph watch loop. Both labels are created by the `create_labels` step.\n\n"
```

becomes:

```python
        "## Labels (keep these)\n\n"
        f"The `{HANDOFF_LABEL}` label routes SRE-filed issues into the creation phase\n"
        "(the Copilot Coding Agent). Both labels are created by the `create_labels` step.\n\n"
```

- [ ] **Step 2: Run the runtime-render tests**

Run: `uv run pytest cli/tests/instance/test_runtime_render.py -q`
Expected: PASS — the body still contains `squad:ready` (from `{HANDOFF_LABEL}`) and `create_labels`.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "docs(instance): reword SRE handoff-label note for the Coding Agent

The squad:ready label now routes issues into the Copilot Coding Agent, not
the retired Ralph watch loop. Part of the ADR 0016 redesign (#71).

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 5: Full-suite verification gate

No code change — this runs the three CI gates over the whole workspace to prove Stage 1 left everything green. If any command fails, fix it inline (re-running the relevant task's steps) before finishing.

**Files:** none.

- [ ] **Step 1: Lint**

Run: `uv run ruff check .`
Expected: `All checks passed!`

- [ ] **Step 2: Import boundaries**

Run: `uv run lint-imports`
Expected: `Contracts: 4 kept, 0 broken.` (the four import-linter contracts in `pyproject.toml`).

- [ ] **Step 3: Full test suite**

Run: `uv run pytest -q`
Expected: all tests pass, 0 failed, 0 errors. (Total count is lower than before by the deleted squad tests — that is expected.)

- [ ] **Step 4: Confirm the squad/AKS footprint is gone (code only — docs are Stage 7)**

Run: `grep -rniE "render_squad_bundle|deploy_squad_ralph|squad_init|aks\.bicep|open_handoff_issue_count|_seed_github_token" cli/ infra/ feature-council/ core/ control-center/`
Expected: no matches.

> Note: `squad:ready` / `HANDOFF_LABEL` (ADR 0007 contract), `squad_governance`, and `squad_maturity` intentionally remain and are NOT part of this grep.

---

## Self-Review (run after the plan, before execution)

**1. Spec coverage** — Stage 1 of the spec's 7-stage table is "retire the AKS/Ralph harness, consolidate runtime to ACA." Tasks 1–3 delete the AKS/Ralph/KEDA provisioning + infra; Task 4 fixes the operator-facing wording; Task 5 gates green. The ACA runtime (`deploy_council`, `containerEnv`, orchestrator app) is untouched and already the only remaining compute — consolidation is achieved by subtraction. No new ACA work is required this stage. ✅

**2. Placeholder scan** — Every edit shows exact before/after code; every command has expected output. No TBD/TODO/"handle edge cases". ✅

**3. Type/name consistency** — Symbols referenced for deletion (`render_squad_bundle`, `KV_SECRET_NAME`, `_seed_github_token`, `open_handoff_issue_count`, `squad_init`, `deploy_squad_ralph`, `aksName`, `squadIdentityClientId`, `tenantId`, `repo_dir`) match the current source exactly. Kept symbols (`HANDOFF_LABEL`, `squad_governance`, `squad_maturity`, `keyVaultName`, `keyVaultAdminAssignment`, `_seed_appconfig`, `_SEED_*`) are explicitly listed in "What is KEPT". The `from pathlib import Path` removal (Task 1) and `repo_dir` removal (Task 2) are the two consequential cleanups that prevent ruff `F401`/`F841`. ✅
