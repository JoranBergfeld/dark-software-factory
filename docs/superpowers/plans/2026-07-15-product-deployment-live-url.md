# Product Deployment to a Live URL Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** After the Copilot Coding Agent builds a product, DSF deploys the finished app to a live public URL and surfaces that URL to the operator.

**Architecture:** `dsf new` provisions a per-product delivery path in `rg-dsf-{product}` (a container registry, an externally-reachable Azure Container App on a public bootstrap image, and a GitHub-Actions OIDC deploy identity) and seeds a `deploy.yml` workflow into the product repo. Every merge to `main` builds the agent's `Dockerfile` (HTTP on `:8080`), pushes it, and `az containerapp update`s the app. `dsf charter implement`/`watch` waits for merge→deploy and prints the live URL; `dsf charter url` reads it on demand. The maturity dial still gates the *merge* (high = auto-merge, low = human approval); deploy runs on every merge.

**Tech Stack:** Python 3.12 (`uv` workspace), Pydantic, argparse, `gh` + `az` CLIs (operator/OIDC auth), Bicep (Azure Container Apps + ACR + user-assigned managed identity + federated credential), GitHub Actions.

**Source of truth:** `docs/superpowers/specs/2026-07-15-product-deployment-live-url-design.md` (approved).

---

## Quality gates (run after every task's implementation step)

Always use `uv`; never bare `python`/`pip`/`pytest`.

- Targeted tests: `uv run pytest <path>::<test> -q`
- Lint the files you touched: `uv run ruff check <files>` (rules `E,F,I,UP,B`, line length 100)
- Import boundaries (only after cross-member import changes): `uv run lint-imports` (expect `Contracts: 4 kept, 0 broken`)

Final gate (Task 12): `uv run pytest -q`, `uv run ruff check .`, `uv run lint-imports`.

---

## File Structure

**Create:**
- `cli/src/dsf/instance/deploy_config.py` — pure builders for the `configure_deploy` step: the `gh` command that creates the `production` GitHub Environment and the `gh variable set` commands that publish the Azure OIDC/ACA coordinates as repo variables. No side effects; unit-testable.
- `cli/tests/instance/test_deploy_config.py` — tests for the builders.
- `docs/adr/0022-product-deploy-leg-live-url.md` — records the deploy-leg decision.

**Modify:**
- `infra/main.bicep` — add the per-product delivery resources (ACR, a dedicated managed environment for the web app, deploy identity + federated credential, external Container App on the bootstrap image, 3 role assignments) and their outputs.
- `cli/src/dsf/instance/bootstrap_issue.py` — state the container/`:8080` paved-road contract in the bootstrap issue.
- `core/src/dsf/charter/constitution.py` — add Core Principle "VI. Deployable Web Service" and bump `_SCHEMA_VERSION` (1 → 2) so existing products re-render.
- `cli/src/dsf/instance/branch_protection.py` — add `DEPLOY_WORKFLOW_PATH`, `CODEOWNERS_PATH`, `deploy_workflow()`, `codeowners_file()`; flip `require_code_owner_review` to `True` so workflow-editing PRs need owner sign-off.
- `cli/src/dsf/instance/provisioner.py` — seed `deploy.yml` + `CODEOWNERS` (clone path and API fallback); add the `configure_deploy` plan step + `apply()` dispatch.
- `cli/src/dsf/cli/charter.py` — add the deploy-watch phase (`_watch_deploy_and_surface_url` + helpers), wire it into `implement`/`watch`, and add the `dsf charter url` command.
- `docs/site/concept/creation.md` — document the deploy leg + live URL.

**Tests touched:**
- `cli/tests/instance/test_bootstrap_issue.py`, `core/tests/charter/test_constitution.py`,
  `cli/tests/instance/test_branch_protection.py`, `cli/tests/instance/test_provisioner.py`,
  `cli/tests/cli/test_charter.py`.

---

## Task 1: Bootstrap-issue paved-road contract

Tell the coding agent the delivery contract: a root `Dockerfile` serving HTTP on `:8080`.

**Files:**
- Modify: `cli/src/dsf/instance/bootstrap_issue.py:31-32`
- Test: `cli/tests/instance/test_bootstrap_issue.py`

- [ ] **Step 1: Write the failing test**

Add to `cli/tests/instance/test_bootstrap_issue.py`:

```python
def test_bootstrap_issue_states_container_deploy_contract():
    from dsf.instance.bootstrap_issue import render_bootstrap_issue

    body = render_bootstrap_issue("todo-app", charter_markdown="# charter\n")
    assert "Dockerfile" in body
    assert "port 8080" in body
    assert "merge to `main`" in body
```

If `render_bootstrap_issue` has a different signature in this file, mirror the call already used by the other tests in the same module (same positional/keyword args) — only the assertions matter here.

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_bootstrap_issue.py::test_bootstrap_issue_states_container_deploy_contract -q`
Expected: FAIL (`assert 'port 8080' in body`).

- [ ] **Step 3: Edit the bootstrap issue text**

In `cli/src/dsf/instance/bootstrap_issue.py`, replace the step-2 line:

```python
        "2. `/speckit.plan` — choose a sensible tech stack and architecture "
        "(a paved-road default is not wired yet — your choice for now).\n"
```

with:

```python
        "2. `/speckit.plan` — choose a sensible tech stack, then package the "
        "product as a container: keep a `Dockerfile` at the repository root that "
        "builds a runnable image serving HTTP on port 8080 (honour `$PORT`, "
        "default 8080). DSF builds that image and deploys it to a live URL on "
        "every merge to `main`, so keep `main` deployable.\n"
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest cli/tests/instance/test_bootstrap_issue.py -q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add cli/src/dsf/instance/bootstrap_issue.py cli/tests/instance/test_bootstrap_issue.py
git commit -m "feat: state container :8080 deploy contract in the bootstrap issue"
```

---

## Task 2: Constitution "Deployable Web Service" principle

Make the delivery contract governing, and bump the schema so existing products re-render.

**Files:**
- Modify: `core/src/dsf/charter/constitution.py:19` (`_SCHEMA_VERSION`) and the `sections` list in `render_constitution` (after Principle V, before `## Additional Constraints`)
- Test: `core/tests/charter/test_constitution.py`

- [ ] **Step 1: Write the failing test**

Add to `core/tests/charter/test_constitution.py`:

```python
def test_constitution_declares_deployable_web_service_principle():
    from dsf.charter.constitution import render_constitution
    from dsf.contracts.charter import Charter

    charter = Charter(
        product="todo-app",
        vision="V",
        target_users="U",
        goals=["g"],
        success_metrics=["m"],
        source_sha="abc123",
        source_ref="main",
    )
    text = render_constitution(charter)
    assert "### VI. Deployable Web Service" in text
    assert "port 8080" in text
    assert "Dockerfile" in text


def test_schema_bump_marks_v1_constitution_stale():
    from dsf.charter.constitution import is_constitution_current, render_constitution
    from dsf.contracts.charter import Charter

    charter = Charter(
        product="todo-app",
        vision="V",
        target_users="U",
        goals=["g"],
        success_metrics=["m"],
        source_sha="abc123",
        source_ref="main",
    )
    current = render_constitution(charter)
    assert is_constitution_current(current, charter) is True
    v1 = current.replace("schema_version=2", "schema_version=1")
    assert is_constitution_current(v1, charter) is False
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest core/tests/charter/test_constitution.py::test_constitution_declares_deployable_web_service_principle core/tests/charter/test_constitution.py::test_schema_bump_marks_v1_constitution_stale -q`
Expected: FAIL (principle text missing; schema still `1`).

- [ ] **Step 3: Bump the schema version**

In `core/src/dsf/charter/constitution.py`, change:

```python
_SCHEMA_VERSION = 1
```

to:

```python
_SCHEMA_VERSION = 2
```

- [ ] **Step 4: Add Principle VI**

In `render_constitution`, insert this element into the `sections` list immediately after the `### V. Shared Vocabulary` block and before the `"## Additional Constraints"` element:

```python
        (
            "### VI. Deployable Web Service (paved road)\n"
            "The product ships as a container that serves HTTP on port 8080. Keep a "
            "`Dockerfile` at the repository root that builds a runnable image and a "
            "process listening on `$PORT` (default 8080). Every merge to `main` is "
            "built and deployed to a live URL, so `main` must always be deployable."
        ),
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `uv run pytest core/tests/charter/test_constitution.py -q`
Expected: PASS (existing render/identity tests still pass; the two new tests pass).

- [ ] **Step 6: Commit**

```bash
git add core/src/dsf/charter/constitution.py core/tests/charter/test_constitution.py
git commit -m "feat: add Deployable Web Service constitution principle (schema v2)"
```

---

## Task 3: Deploy workflow + CODEOWNERS builders and code-owner ruleset

Add the seeded `deploy.yml` content, a `CODEOWNERS` guarding `.github/workflows/*`, and require code-owner review so workflow edits need the owner even under high maturity.

**Files:**
- Modify: `cli/src/dsf/instance/branch_protection.py` (add constants + two builders; flip `require_code_owner_review`)
- Test: `cli/tests/instance/test_branch_protection.py`

- [ ] **Step 1: Write the failing tests**

Add to `cli/tests/instance/test_branch_protection.py` (extend the existing imports from `dsf.instance.branch_protection` to include `CODEOWNERS_PATH`, `DEPLOY_WORKFLOW_PATH`, `codeowners_file`, `deploy_workflow`):

```python
def test_ruleset_requires_code_owner_review():
    params = _rule(ruleset_payload(_spec("high")), "pull_request")["parameters"]
    assert params["require_code_owner_review"] is True


def test_deploy_workflow_targets_main_and_uses_oidc():
    wf = deploy_workflow()
    assert wf.startswith("name: deploy\n")
    assert "id-token: write" in wf
    assert "azure/login@v2" in wf
    assert "az containerapp update" in wf
    assert "targetPort" not in wf  # port is baked into infra, not the workflow
    assert "environment:" in wf and "name: production" in wf
    assert wf.endswith("\n")


def test_deploy_workflow_path_is_under_github_workflows():
    assert DEPLOY_WORKFLOW_PATH == ".github/workflows/deploy.yml"


def test_codeowners_guards_workflows_for_owner():
    text = codeowners_file("acme")
    assert ".github/workflows/ @acme" in text
    assert CODEOWNERS_PATH == ".github/CODEOWNERS"
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest cli/tests/instance/test_branch_protection.py -q`
Expected: FAIL (`ImportError` for the new names; `require_code_owner_review` is `False`).

- [ ] **Step 3: Flip the code-owner rule**

In `cli/src/dsf/instance/branch_protection.py`, inside `ruleset_payload`, change:

```python
                    "require_code_owner_review": False,
```

to:

```python
                    "require_code_owner_review": True,
```

Also update the module docstring's dial description to note that a CODEOWNERS match (workflow edits) needs owner approval even at `high`. Change the `high` bullet to:

```python
- ``high`` — require 0 reviews but still the green ``ci`` check; auto-merge on,
  so a PR merges itself once ``ci`` is green (no human) UNLESS it edits a
  CODEOWNERS-guarded path (``.github/workflows/*``), which always needs the
  product owner's review.
```

- [ ] **Step 4: Add the constants and builders**

Append to `cli/src/dsf/instance/branch_protection.py` (after `baseline_ci_workflow`):

```python
#: Path of the deploy workflow seeded into a freshly provisioned repo.
DEPLOY_WORKFLOW_PATH = ".github/workflows/deploy.yml"

#: Path of the CODEOWNERS file guarding the privileged deploy path.
CODEOWNERS_PATH = ".github/CODEOWNERS"


def deploy_workflow() -> str:
    """Return the GitHub Actions workflow that deploys the product on merge to main.

    Runs on every push to ``main`` (and manual dispatch). Authenticates to Azure
    with OIDC (the federated deploy identity provisioned by ``dsf new``; no stored
    secrets — only non-secret repo *variables*), builds the product's root
    ``Dockerfile`` on the runner, pushes it to the per-product ACR, then
    ``az containerapp update``\\ s the product app to the new image and reads back
    its public FQDN. The ``production`` environment records the deployment URL so
    ``dsf`` and the repo's Environments tab surface it. The image serves HTTP on the
    port baked into the container app (8080); the workflow never sets the port.
    """
    return (
        "name: deploy\n"
        "on:\n"
        "  push:\n"
        "    branches: [main]\n"
        "  workflow_dispatch: {}\n"
        "permissions:\n"
        "  contents: read\n"
        "  id-token: write\n"
        "  deployments: write\n"
        "concurrency:\n"
        "  group: deploy-${{ github.ref }}\n"
        "  cancel-in-progress: true\n"
        "jobs:\n"
        "  deploy:\n"
        "    runs-on: ubuntu-latest\n"
        "    environment:\n"
        "      name: production\n"
        "      url: ${{ steps.deploy.outputs.url }}\n"
        "    steps:\n"
        "      - uses: actions/checkout@v4\n"
        "      - name: Require a Dockerfile\n"
        "        run: >-\n"
        "          test -f Dockerfile ||\n"
        "          { echo '::error::No Dockerfile at repo root; DSF deploys a "
        "container on :8080.'; exit 1; }\n"
        "      - uses: azure/login@v2\n"
        "        with:\n"
        "          client-id: ${{ vars.AZURE_CLIENT_ID }}\n"
        "          tenant-id: ${{ vars.AZURE_TENANT_ID }}\n"
        "          subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}\n"
        "      - name: Build and push image\n"
        "        id: build\n"
        "        run: |\n"
        "          set -euo pipefail\n"
        "          IMAGE=\"${{ vars.DSF_ACR_NAME }}.azurecr.io/"
        "${{ github.event.repository.name }}:${{ github.sha }}\"\n"
        "          az acr login --name \"${{ vars.DSF_ACR_NAME }}\"\n"
        "          docker build -t \"$IMAGE\" .\n"
        "          docker push \"$IMAGE\"\n"
        "          echo \"image=$IMAGE\" >> \"$GITHUB_OUTPUT\"\n"
        "      - name: Deploy to Azure Container Apps\n"
        "        id: deploy\n"
        "        run: |\n"
        "          set -euo pipefail\n"
        "          az extension add --name containerapp --only-show-errors\n"
        "          az containerapp update \\\n"
        "            --name \"${{ vars.DSF_ACA_APP }}\" \\\n"
        "            --resource-group \"${{ vars.DSF_ACA_RG }}\" \\\n"
        "            --image \"${{ steps.build.outputs.image }}\"\n"
        "          FQDN=$(az containerapp show \\\n"
        "            --name \"${{ vars.DSF_ACA_APP }}\" \\\n"
        "            --resource-group \"${{ vars.DSF_ACA_RG }}\" \\\n"
        "            --query properties.configuration.ingress.fqdn -o tsv)\n"
        "          echo \"url=https://$FQDN\" >> \"$GITHUB_OUTPUT\"\n"
        "          echo \"### Deployed\" >> \"$GITHUB_STEP_SUMMARY\"\n"
        "          echo \"https://$FQDN\" >> \"$GITHUB_STEP_SUMMARY\"\n"
    )


def codeowners_file(owner: str) -> str:
    """Return a CODEOWNERS assigning the privileged deploy path to the product owner.

    CODEOWNERS review only triggers on PRs touching a matched path, so ordinary
    product PRs are unaffected; a PR editing ``.github/workflows/*`` needs the
    owner's approval even under the ``high`` (auto-merge) dial. Guards the whole
    workflows directory so neither ``ci.yml`` nor ``deploy.yml`` can be silently
    rewritten by the coding agent.
    """
    return (
        "# Guard the privileged CI/CD path: workflow edits need the product owner.\n"
        f".github/workflows/ @{owner}\n"
    )
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_branch_protection.py -q`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/instance/branch_protection.py cli/tests/instance/test_branch_protection.py
git commit -m "feat: add deploy.yml + CODEOWNERS builders and require code-owner review"
```

---

## Task 4: Deploy-config command builders

Pure builders for the `configure_deploy` step: create the `production` GitHub Environment and publish the Azure/ACA coordinates as non-secret repo variables.

**Files:**
- Create: `cli/src/dsf/instance/deploy_config.py`
- Test: `cli/tests/instance/test_deploy_config.py`

- [ ] **Step 1: Write the failing tests**

Create `cli/tests/instance/test_deploy_config.py`:

```python
"""Tests for the deploy-config command builders."""

from __future__ import annotations

from dsf.instance.deploy_config import (
    github_environment_command,
    repo_variable_commands,
)

_OUTPUTS = {
    "deployIdentityClientId": "client-123",
    "deployTenantId": "tenant-456",
    "deploySubscriptionId": "sub-789",
    "productAcrName": "demoacrxyz",
    "productAppEnvName": "demo-cae-xyz",
    "productAppName": "demo-app",
    "productAppResourceGroup": "rg-dsf-demo",
}


def test_github_environment_command_creates_production():
    assert github_environment_command("acme/demo") == [
        "gh", "api", "--method", "PUT", "/repos/acme/demo/environments/production",
    ]


def test_repo_variable_commands_publish_all_coordinates():
    cmds = repo_variable_commands("acme/demo", _OUTPUTS)
    pairs = {c[3]: c[7] for c in cmds}  # gh variable set NAME --repo R --body VALUE
    assert pairs == {
        "AZURE_CLIENT_ID": "client-123",
        "AZURE_TENANT_ID": "tenant-456",
        "AZURE_SUBSCRIPTION_ID": "sub-789",
        "DSF_ACR_NAME": "demoacrxyz",
        "DSF_ACA_ENV": "demo-cae-xyz",
        "DSF_ACA_APP": "demo-app",
        "DSF_ACA_RG": "rg-dsf-demo",
    }
    for c in cmds:
        assert c[:3] == ["gh", "variable", "set"]
        assert c[4:6] == ["--repo", "acme/demo"]
        assert c[6] == "--body"


def test_repo_variable_commands_tolerate_missing_output():
    cmds = repo_variable_commands("acme/demo", {})
    assert all(c[7] == "" for c in cmds)
    assert len(cmds) == 7
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest cli/tests/instance/test_deploy_config.py -q`
Expected: FAIL (`ModuleNotFoundError: dsf.instance.deploy_config`).

- [ ] **Step 3: Create the builders module**

Create `cli/src/dsf/instance/deploy_config.py`:

```python
"""Builders for the ``configure_deploy`` provisioning step.

Wire a freshly provisioned product repo's ``deploy.yml`` to Azure: create the
``production`` GitHub Environment (so the deployment URL is recorded and the OIDC
federated-credential subject ``environment:production`` matches) and publish the
Azure OIDC + Container Apps coordinates as **non-secret repo variables** (the
deploy identity is keyless OIDC, so nothing here is a secret). Pure: every builder
returns a ``gh`` argv the caller runs under the operator's interactive auth.
"""

from __future__ import annotations

from collections.abc import Mapping

#: Repo variable name -> bicep deploy-output key it is populated from.
_VARIABLE_OUTPUTS: dict[str, str] = {
    "AZURE_CLIENT_ID": "deployIdentityClientId",
    "AZURE_TENANT_ID": "deployTenantId",
    "AZURE_SUBSCRIPTION_ID": "deploySubscriptionId",
    "DSF_ACR_NAME": "productAcrName",
    "DSF_ACA_ENV": "productAppEnvName",
    "DSF_ACA_APP": "productAppName",
    "DSF_ACA_RG": "productAppResourceGroup",
}


def github_environment_command(repo: str) -> list[str]:
    """Return the ``gh`` command creating the ``production`` Environment (idempotent)."""
    return [
        "gh", "api", "--method", "PUT",
        f"/repos/{repo}/environments/production",
    ]


def repo_variable_commands(repo: str, outputs: Mapping[str, str]) -> list[list[str]]:
    """Return ``gh variable set`` commands publishing the deploy coordinates.

    ``outputs`` is the Azure deployment's output map (bicep output name -> value).
    A missing key yields an empty value rather than raising, so a partial re-run
    still produces deterministic commands the operator can inspect.
    """
    commands: list[list[str]] = []
    for variable, output_key in _VARIABLE_OUTPUTS.items():
        value = outputs.get(output_key, "")
        commands.append(
            ["gh", "variable", "set", variable, "--repo", repo, "--body", value]
        )
    return commands
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_deploy_config.py -q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add cli/src/dsf/instance/deploy_config.py cli/tests/instance/test_deploy_config.py
git commit -m "feat: add configure_deploy command builders (env + repo variables)"
```

---

## Task 5: Infra — per-product delivery resources + outputs

Add the ACR, a dedicated managed environment, deploy identity + federated credential, external Container App (bootstrap image, `:8080`), and 3 role assignments to `infra/main.bicep`, plus outputs the `configure_deploy` step and CLI consume. The web app gets its **own** managed environment (blast-radius isolation from the factory orchestrator, per the design).

Read `.github/instructions/bicep-code-best-practices.instructions.md` first (lowerCamelCase symbolic names, avoid `name` in symbolic names, use symbolic references not `dependsOn`/`resourceId`, `uniqueString()` with a prefix for names, never secrets in outputs).

**Files:**
- Modify: `infra/main.bicep` (add role-id vars near line 103; add a resources block after the `orchestratorApp` resource ~line 463; add outputs after line 502)

- [ ] **Step 1: Add the role-definition id variables**

In `infra/main.bicep`, after the existing role-id vars (after line 103, `cognitiveServicesOpenAIUserRoleId`), add:

```bicep
var acrPushRoleId = '8311e382-0749-4cb8-b61a-304f252e45ec' // AcrPush
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d' // AcrPull
var contributorRoleId = 'b24988ac-6180-42a0-ab88-20f7382dd24c' // Contributor
```

- [ ] **Step 2: Add the delivery-name variables**

After the `keyVaultName` var (line 89), add:

```bicep
// ACR names are alphanumeric only (no hyphens), globally unique, capped at 50.
var productAcrName = take('${namePrefix}acr${suffix}', 50)
// Public bootstrap image so the app + its FQDN exist before the first real deploy.
var deployBootstrapImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
```

- [ ] **Step 3: Add the delivery resources**

In `infra/main.bicep`, immediately after the `orchestratorApp` resource block (before the `// Outputs` header ~line 463), add:

```bicep
// ---------------------------------------------------------------------------
// Product delivery: per-product ACR + a dedicated managed environment +
// externally reachable web app + a GitHub Actions deploy identity (OIDC federated
// credential). The coding agent ships a Dockerfile (HTTP on :8080); the product
// repo's deploy.yml builds + pushes to this ACR and `az containerapp update`s this
// app on every merge to main. The app starts on a public hello-world image and
// 503s until the first real deploy. A dedicated environment keeps the product's
// public app isolated from the factory orchestrator. Everything lives in
// rg-dsf-<product>, so `dsf offboard` (RG delete) removes the whole delivery path.
// Requires githubRepository to be set (dsf new always sets it).
// ---------------------------------------------------------------------------

resource productAcr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: productAcrName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

resource productAppEnv 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${namePrefix}-app-cae-${suffix}'
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

resource deployIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${namePrefix}-deploy-${suffix}'
  location: location
  tags: tags
}

resource deployFederatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  parent: deployIdentity
  name: 'github-actions-production'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:environment:production'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

resource productApp 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${namePrefix}-app'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${deployIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: productAppEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: productAcr.properties.loginServer
          identity: deployIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'app'
          image: deployBootstrapImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

resource deployAcrPushAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(productAcr.id, deployIdentity.id, acrPushRoleId)
  scope: productAcr
  properties: {
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPushRoleId)
  }
}

resource deployAcrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(productAcr.id, deployIdentity.id, acrPullRoleId)
  scope: productAcr
  properties: {
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
  }
}

resource deployAppContributorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(productApp.id, deployIdentity.id, contributorRoleId)
  scope: productApp
  properties: {
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRoleId)
  }
}
```

- [ ] **Step 4: Add the outputs**

At the end of `infra/main.bicep` (after `logAnalyticsId`), add:

```bicep
@description('Name of the per-product container registry (deploy.yml pushes here).')
output productAcrName string = productAcr.name

@description('Name of the per-product web Container App the deploy workflow updates.')
output productAppName string = productApp.name

@description('Managed environment hosting the product web app.')
output productAppEnvName string = productAppEnv.name

@description('Resource group hosting the product web app (for az containerapp update).')
output productAppResourceGroup string = resourceGroup().name

@description('Client ID of the GitHub Actions deploy identity (OIDC federated).')
output deployIdentityClientId string = deployIdentity.properties.clientId

@description('Tenant ID for the GitHub Actions OIDC login.')
output deployTenantId string = subscription().tenantId

@description('Subscription ID for the GitHub Actions OIDC login.')
output deploySubscriptionId string = subscription().subscriptionId

@description('Public FQDN of the product web app (503s until the first deploy).')
output productAppFqdn string = productApp.properties.configuration.ingress.fqdn
```

- [ ] **Step 5: Update the file header comment**

In the top banner comment of `infra/main.bicep`, extend the resource summary. Change the line:

```bicep
// Container Apps environment + a single
// no-ingress orchestrator Container App. DSF is pull-only (ADR 0014): the orchestrator
```

to:

```bicep
// Container Apps environment + a
// no-ingress orchestrator Container App, plus the per-product delivery path (a
// container registry, an externally reachable web app on :8080, and a GitHub Actions
// OIDC deploy identity) that ships the finished product to a live URL. DSF is
// pull-only for intake (ADR 0014): the orchestrator
```

- [ ] **Step 6: Compile the template to verify it is valid**

Run: `az bicep build --file infra/main.bicep --outfile /dev/null`
Expected: exit 0, no errors. (If `az` is unavailable, run `bicep build infra/main.bicep --outfile /dev/null`.)

If it reports a linter warning about `productAppFqdn` (ingress may be null), it is a warning not an error; the output is only read post-deploy when ingress exists. Do not gate on warnings.

- [ ] **Step 7: Commit**

```bash
git add infra/main.bicep
git commit -m "feat: provision per-product delivery path (ACR + web app + OIDC identity)"
```

---

## Task 6: Seed deploy.yml + CODEOWNERS in the provisioner

Seed the two new files alongside the baseline CI workflow — both from the clone and via the Contents-API fallback.

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py` (imports ~line 33-38; `_seed_repo_from_clone` ~line 833-835; `_seed_ci_workflow_via_api` ~line 855-880)
- Test: `cli/tests/instance/test_provisioner.py`

- [ ] **Step 1: Write the failing test**

Add to `cli/tests/instance/test_provisioner.py` (it already imports `InstanceProvisioner` and a `_spec` helper; reuse them):

```python
def test_seed_repo_from_clone_writes_deploy_and_codeowners(tmp_path):
    from types import SimpleNamespace

    from dsf.instance.branch_protection import (
        CODEOWNERS_PATH,
        DEPLOY_WORKFLOW_PATH,
        codeowners_file,
        deploy_workflow,
    )

    def _run(cmd, **kwargs):
        # `git status --porcelain` is read for a diff; return "clean" so the
        # commit/push is skipped. Everything else is a no-op recorder.
        return SimpleNamespace(stdout="", returncode=0)

    prov = InstanceProvisioner(_spec(), run=_run)
    prov._seed_repo_from_clone(str(tmp_path))

    deploy = tmp_path / DEPLOY_WORKFLOW_PATH
    codeowners = tmp_path / CODEOWNERS_PATH
    assert deploy.read_text(encoding="utf-8") == deploy_workflow()
    assert codeowners.read_text(encoding="utf-8") == codeowners_file(prov.spec.owner)
```

If the `_spec()` helper in this file requires an `owner`, it already sets one (the module's existing tests use it); no change needed.

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_provisioner.py::test_seed_repo_from_clone_writes_deploy_and_codeowners -q`
Expected: FAIL (`FileNotFoundError` — deploy.yml not written).

- [ ] **Step 3: Extend the imports**

In `cli/src/dsf/instance/provisioner.py`, the `from dsf.instance.branch_protection import (...)` block currently imports `CI_WORKFLOW_PATH`, `RULESET_NAME`, `baseline_ci_workflow`, etc. Add the four new names:

```python
from dsf.instance.branch_protection import (
    CI_WORKFLOW_PATH,
    CODEOWNERS_PATH,
    DEPLOY_WORKFLOW_PATH,
    RULESET_NAME,
    RULESET_UNSUPPORTED_RESULT,
    auto_merge_command,
    baseline_ci_workflow,
    codeowners_file,
    deploy_workflow,
    is_unsupported_ruleset_error,
    ruleset_payload,
)
```

(Keep whichever existing names are already imported; only add `CODEOWNERS_PATH`, `DEPLOY_WORKFLOW_PATH`, `codeowners_file`, `deploy_workflow`. Preserve import sorting — ruff `I` will flag order.)

- [ ] **Step 4: Write the deploy.yml + CODEOWNERS in the clone path**

In `_seed_repo_from_clone`, after the block that writes the CI workflow:

```python
        workflow = Path(clone_dir) / CI_WORKFLOW_PATH
        workflow.parent.mkdir(parents=True, exist_ok=True)
        workflow.write_text(baseline_ci_workflow(), encoding="utf-8")
```

add:

```python
        deploy = Path(clone_dir) / DEPLOY_WORKFLOW_PATH
        deploy.parent.mkdir(parents=True, exist_ok=True)
        deploy.write_text(deploy_workflow(), encoding="utf-8")

        codeowners = Path(clone_dir) / CODEOWNERS_PATH
        codeowners.parent.mkdir(parents=True, exist_ok=True)
        codeowners.write_text(codeowners_file(self.spec.owner), encoding="utf-8")
```

Also update the commit message on the `git commit` call in this method (it seeds three artifacts now):

```python
                "commit", "-m",
                "chore: seed spec kit scaffold, ci + deploy workflows, CODEOWNERS",
```

- [ ] **Step 5: Extend the Contents-API fallback**

Replace the whole `_seed_ci_workflow_via_api` method with a version that seeds all three files via a shared helper:

```python
    def _seed_ci_workflow_via_api(self) -> None:
        """Seed the baseline ci + deploy workflows and CODEOWNERS via the Contents
        API (no clone). Idempotent per file: skips one already present so a retry
        doesn't 422 on a missing blob sha."""
        self._seed_file_via_api(
            CI_WORKFLOW_PATH, baseline_ci_workflow(),
            "chore: seed baseline ci workflow",
        )
        self._seed_file_via_api(
            DEPLOY_WORKFLOW_PATH, deploy_workflow(),
            "chore: seed deploy workflow",
        )
        self._seed_file_via_api(
            CODEOWNERS_PATH, codeowners_file(self.spec.owner),
            "chore: seed CODEOWNERS guarding the deploy path",
        )

    def _seed_file_via_api(self, path: str, content: str, message: str) -> None:
        """PUT ``content`` to ``path`` on ``main`` via the Contents API if absent."""
        repo = self.spec.github_repo()
        try:
            self._run(
                ["gh", "api", f"/repos/{repo}/contents/{path}", "--jq", ".sha"],
                check=True, capture_output=True, text=True,
            )
            return
        except subprocess.CalledProcessError:
            pass
        encoded = base64.b64encode(content.encode("utf-8")).decode("ascii")
        self._run(
            [
                "gh", "api", "--method", "PUT",
                f"/repos/{repo}/contents/{path}",
                "-f", f"message={message}",
                "-f", f"content={encoded}",
                "-f", "branch=main",
            ],
            check=True,
        )
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -q`
Expected: PASS (new test passes; existing seed/API-fallback tests still pass).

- [ ] **Step 7: Commit**

```bash
git add cli/src/dsf/instance/provisioner.py cli/tests/instance/test_provisioner.py
git commit -m "feat: seed deploy.yml + CODEOWNERS during repo provisioning"
```

---

## Task 7: `configure_deploy` provisioning step

Add the plan step that creates the `production` Environment and publishes the repo variables from the bicep deploy outputs.

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py` (imports; `plan()` step list ~line 382-389; `apply()` dispatch after the `branch_protection` branch ~line 577-585)
- Test: `cli/tests/instance/test_provisioner.py`

- [ ] **Step 1: Write the failing tests**

Add to `cli/tests/instance/test_provisioner.py`:

```python
def test_plan_includes_configure_deploy_after_branch_protection():
    names = [s.name for s in InstanceProvisioner(_spec()).plan().steps]
    assert "configure_deploy" in names
    assert names.index("branch_protection") < names.index("configure_deploy")
    assert names.index("configure_deploy") < names.index("deploy_sre_agent")


def test_configure_deploy_dry_run_shells_out_to_nothing():
    calls = []

    def _run(cmd, **kwargs):
        calls.append(cmd)
        from types import SimpleNamespace

        return SimpleNamespace(stdout="", returncode=0)

    prov = InstanceProvisioner(_spec(), run=_run)
    plan = prov.plan()
    step = next(s for s in plan.steps if s.name == "configure_deploy")
    from dsf.instance.spec import AzureProvisionResult

    azure = AzureProvisionResult(
        resource_group="rg-dsf-demo",
        deployment_name="dsf-demo",
        location="swedencentral",
        outputs={"deployIdentityClientId": "cid", "productAppName": "demo-app"},
    )
    prov.apply(step, plan, executed=True, execute=False, azure_result=azure)
    assert step.result == "configured (dry-run)"
    assert calls == []


def test_configure_deploy_creates_environment_and_variables():
    calls = []

    def _run(cmd, **kwargs):
        calls.append(cmd)
        from types import SimpleNamespace

        return SimpleNamespace(stdout="", returncode=0)

    prov = InstanceProvisioner(_spec(), run=_run)
    plan = prov.plan()
    step = next(s for s in plan.steps if s.name == "configure_deploy")
    from dsf.instance.spec import AzureProvisionResult

    azure = AzureProvisionResult(
        resource_group="rg-dsf-demo",
        deployment_name="dsf-demo",
        location="swedencentral",
        outputs={
            "deployIdentityClientId": "cid",
            "deployTenantId": "tid",
            "deploySubscriptionId": "sid",
            "productAcrName": "demoacr",
            "productAppEnvName": "demo-cae",
            "productAppName": "demo-app",
            "productAppResourceGroup": "rg-dsf-demo",
        },
    )
    prov.apply(step, plan, executed=True, execute=True, azure_result=azure)
    repo = prov.spec.github_repo()
    assert ["gh", "api", "--method", "PUT",
            f"/repos/{repo}/environments/production"] in calls
    var_calls = [c for c in calls if c[:3] == ["gh", "variable", "set"]]
    assert {c[3] for c in var_calls} == {
        "AZURE_CLIENT_ID", "AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID",
        "DSF_ACR_NAME", "DSF_ACA_ENV", "DSF_ACA_APP", "DSF_ACA_RG",
    }
    assert step.result == "configured"
```

Confirm `apply`'s signature matches these keyword args by checking the existing `apply(` calls in this test file (they already pass `plan`, `executed`, `execute`, `azure_result`). Mirror exactly what those existing tests pass.

- [ ] **Step 2: Update the step-order assertion**

In the existing `test_plan_step_order_and_names`, insert `"configure_deploy"` between `"branch_protection"` and `"deploy_sre_agent"`:

```python
        "branch_protection",
        "configure_deploy",
        "deploy_sre_agent",
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k "configure_deploy or step_order" -q`
Expected: FAIL (`configure_deploy` step missing; dispatch not handled).

- [ ] **Step 4: Import the builders**

In `cli/src/dsf/instance/provisioner.py`, add near the other `dsf.instance` imports:

```python
from dsf.instance.deploy_config import (
    github_environment_command,
    repo_variable_commands,
)
```

- [ ] **Step 5: Add the plan step**

In `plan()`, insert this `ProvisionStep` into the `steps` list immediately after the `branch_protection` step and before `deploy_sre_agent`:

```python
            ProvisionStep(
                name="configure_deploy",
                description=(
                    f"Wire {s.github_repo()}'s deploy workflow to Azure: create the "
                    "'production' GitHub Environment and set the AZURE_*/DSF_ACA_* "
                    "repo variables from the bicep deploy outputs (OIDC, no secrets)"
                ),
            ),
```

- [ ] **Step 6: Add the apply() dispatch**

In `apply()`, add this branch immediately after the `elif step.name == "branch_protection":` block and before `elif step.name == "deploy_sre_agent":`:

```python
        elif step.name == "configure_deploy":
            if not execute:
                step.result = "configured (dry-run)"
            else:
                outputs = azure_result.outputs if azure_result else {}
                repo = self.spec.github_repo()
                self._run(github_environment_command(repo), check=True)
                for cmd in repo_variable_commands(repo, outputs):
                    self._run(cmd, check=True)
                step.executed, step.result = True, "configured"
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -q`
Expected: PASS.

- [ ] **Step 8: Verify import boundaries**

Run: `uv run lint-imports`
Expected: `Contracts: 4 kept, 0 broken` (deploy_config lives in the same member; no boundary change).

- [ ] **Step 9: Commit**

```bash
git add cli/src/dsf/instance/provisioner.py cli/tests/instance/test_provisioner.py
git commit -m "feat: add configure_deploy provisioning step wiring repo to Azure OIDC"
```

---

## Task 8: CLI deploy-watch phase (surface the live URL)

After review hand-off, wait for merge→deploy and surface the live URL: print it, record it in the owner App Config index, and comment it on the bootstrap issue.

**Files:**
- Modify: `cli/src/dsf/cli/charter.py` (new helpers + `_watch_deploy_and_surface_url`; wire into `_cmd_charter_implement` ~line 901 and `_cmd_charter_watch` ~line 926)
- Test: `cli/tests/cli/test_charter.py`

- [ ] **Step 1: Write the failing tests**

Add to `cli/tests/cli/test_charter.py`:

```python
def _deploy_env(
    monkeypatch,
    *,
    pr,
    merge_sha,
    conclusions,
    live_url="https://demo.example.net",
):
    """Patch the deploy-phase helpers; return a dict recording side effects."""
    seen = {"recorded": None, "commented": None}
    conc = iter(conclusions)
    monkeypatch.setattr("dsf.cli.charter._find_agent_pr", lambda repo, issue: pr)
    monkeypatch.setattr(
        "dsf.cli.charter._merged_commit_sha", lambda repo, issue: merge_sha
    )
    monkeypatch.setattr(
        "dsf.cli.charter._deploy_run_conclusion", lambda repo, sha: next(conc)
    )
    monkeypatch.setattr("dsf.cli.charter._read_live_url", lambda product: live_url)
    monkeypatch.setattr(
        "dsf.cli.charter._record_deploy_url",
        lambda product, url: seen.__setitem__("recorded", (product, url)),
    )
    monkeypatch.setattr(
        "dsf.cli.charter._comment_deploy_url",
        lambda repo, issue, url: seen.__setitem__("commented", (issue, url)),
    )
    return seen


def test_deploy_watch_surfaces_url_on_success(monkeypatch, capsys):
    merged = {"number": 8, "url": "https://x/pull/8", "is_draft": False, "state": "MERGED"}
    seen = _deploy_env(
        monkeypatch, pr=merged, merge_sha="deadbeef", conclusions=["success"]
    )
    rc = charter._watch_deploy_and_surface_url(
        "org/alpha", "alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    out = capsys.readouterr().out
    assert rc == 0
    assert "https://demo.example.net" in out
    assert seen["recorded"] == ("alpha", "https://demo.example.net")
    assert seen["commented"] == (7, "https://demo.example.net")


def test_deploy_watch_waits_for_merge_then_deploys(monkeypatch, capsys):
    merged = {"number": 8, "url": "https://x/pull/8", "is_draft": False, "state": "MERGED"}
    seen = _deploy_env(
        monkeypatch, pr=merged, merge_sha="deadbeef", conclusions=[None, "success"]
    )
    rc = charter._watch_deploy_and_surface_url(
        "org/alpha", "alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    assert rc == 0
    assert seen["recorded"] is not None


def test_deploy_watch_returns_when_pr_closed_unmerged(monkeypatch, capsys):
    closed = {"number": 8, "url": "https://x/pull/8", "is_draft": False, "state": "CLOSED"}
    seen = _deploy_env(monkeypatch, pr=closed, merge_sha=None, conclusions=[])
    rc = charter._watch_deploy_and_surface_url(
        "org/alpha", "alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    assert rc == 0
    assert seen["recorded"] is None


def test_deploy_watch_reports_failed_deploy(monkeypatch, capsys):
    merged = {"number": 8, "url": "https://x/pull/8", "is_draft": False, "state": "MERGED"}
    _deploy_env(
        monkeypatch, pr=merged, merge_sha="deadbeef", conclusions=["failure"]
    )
    rc = charter._watch_deploy_and_surface_url(
        "org/alpha", "alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    out = capsys.readouterr().out
    assert rc == 1
    assert "failure" in out


def test_deploy_watch_times_out(monkeypatch, capsys):
    open_pr = {"number": 8, "url": "https://x/pull/8", "is_draft": False, "state": "OPEN"}
    _deploy_env(monkeypatch, pr=open_pr, merge_sha=None, conclusions=[])
    clock = iter([0.0, 5.0, 999.0])
    rc = charter._watch_deploy_and_surface_url(
        "org/alpha", "alpha", 7, timeout=10.0, poll_interval=0.0,
        sleep=lambda s: None, clock=lambda: next(clock),
    )
    assert rc == 2
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest cli/tests/cli/test_charter.py -k deploy_watch -q`
Expected: FAIL (`AttributeError: module 'dsf.cli.charter' has no attribute '_watch_deploy_and_surface_url'`).

- [ ] **Step 3: Add the deploy-phase helpers**

In `cli/src/dsf/cli/charter.py`, add these module-level helpers near `_find_agent_pr` (after `_request_copilot_review`):

```python
def _merged_commit_sha(repo: str, issue_number: int) -> str | None:
    """Merge commit SHA of the coding agent's PR once it is MERGED, else None."""
    pr = _find_agent_pr(repo, issue_number)
    if pr is None or pr["state"] != "MERGED":
        return None
    return _pr_merge_commit(repo, pr["number"])


def _pr_merge_commit(repo: str, number: int) -> str | None:
    """Return a merged PR's merge-commit oid via ``gh pr view`` (or None)."""
    import json
    import subprocess

    result = subprocess.run(
        ["gh", "pr", "view", str(number), "--repo", repo, "--json", "mergeCommit"],
        check=True, capture_output=True, text=True,
    )
    merge = (json.loads(result.stdout) or {}).get("mergeCommit") or {}
    return merge.get("oid") or None


def _deploy_run_conclusion(repo: str, head_sha: str) -> str | None:
    """Conclusion of the latest ``deploy.yml`` run for ``head_sha`` (None if pending).

    Returns e.g. ``"success"``/``"failure"``; ``None`` while the run is queued or
    in progress, or when no run exists yet for the commit.
    """
    import subprocess

    owner, _, name = repo.partition("/")
    result = subprocess.run(
        [
            "gh", "api",
            f"/repos/{owner}/{name}/actions/workflows/deploy.yml/runs?head_sha={head_sha}",
            "--jq", '.workflow_runs[0].conclusion // ""',
        ],
        check=True, capture_output=True, text=True,
    )
    return result.stdout.strip() or None


def _read_live_url(product: str) -> str | None:
    """Public https URL of the product's Container App, read live via ``az`` (or None).

    Derives the app name (``<name_prefix>-app``) and resource group from the
    product's instance manifest, then reads the ingress FQDN. Returns ``None`` when
    the manifest is missing or ingress has no FQDN yet.
    """
    import subprocess

    try:
        manifest = read_manifest(product)
    except (OSError, ValueError):
        return None
    spec = manifest.spec
    result = subprocess.run(
        [
            "az", "containerapp", "show",
            "--name", f"{spec.name_prefix}-app",
            "--resource-group", spec.resource_group(),
            "--query", "properties.configuration.ingress.fqdn",
            "--output", "tsv",
        ],
        check=True, capture_output=True, text=True,
    )
    fqdn = result.stdout.strip()
    return f"https://{fqdn}" if fqdn else None


def _record_deploy_url(product: str, url: str) -> None:
    """Record the live URL in the owner App Config index (key ``DSF_DEPLOY_URL``)."""
    import os

    from dsf.config.owner_index import OWNER_APPCONFIG_ENV, publish_runtime_config

    endpoint = (os.environ.get(OWNER_APPCONFIG_ENV) or "").strip()
    if not endpoint:
        return
    publish_runtime_config(endpoint, product, {"DSF_DEPLOY_URL": url})


def _recorded_deploy_url(product: str) -> str | None:
    """Last recorded live URL from the owner App Config index, or None."""
    import os

    from dsf.config.owner_index import OWNER_APPCONFIG_ENV, read_runtime_config

    endpoint = (os.environ.get(OWNER_APPCONFIG_ENV) or "").strip()
    if not endpoint:
        return None
    return read_runtime_config(endpoint, product).get("DSF_DEPLOY_URL") or None


def _comment_deploy_url(repo: str, issue_number: int, url: str) -> None:
    """Comment the live URL on the bootstrap issue via the operator's gh token."""
    import subprocess

    subprocess.run(
        [
            "gh", "issue", "comment", str(issue_number), "--repo", repo,
            "--body", f"Deployed to a live environment: {url}",
        ],
        check=True, capture_output=True, text=True,
    )
```

- [ ] **Step 4: Add the deploy-watch loop**

In `cli/src/dsf/cli/charter.py`, add this function immediately after `_watch_and_request_review`:

```python
def _watch_deploy_and_surface_url(
    repo: str,
    product: str,
    issue_number: int,
    *,
    timeout: float | None,
    poll_interval: float,
    sleep=time.sleep,
    clock=time.monotonic,
    out=print,
) -> int:
    """Wait for the merge->deploy, then surface the product's live URL.

    Polls the coding agent's PR until it MERGES, then waits for the product repo's
    ``deploy`` workflow to finish for the merge commit. On success it reads the
    app's public FQDN, prints it, records it in the owner App Config index, and
    comments it on the bootstrap issue. Returns ``0`` on success or when there is
    nothing to deploy (PR closed unmerged), ``1`` if the deploy workflow failed,
    and ``2`` on timeout (resumable via ``dsf charter watch`` / ``dsf charter
    url``). Transient GitHub/Azure errors are logged and retried until the timeout.
    """
    import json
    import subprocess

    transient = (
        subprocess.CalledProcessError,
        RuntimeError,
        json.JSONDecodeError,
        KeyError,
        TypeError,
    )
    start = clock()
    last_status = ""
    failed_conclusions = ("failure", "cancelled", "timed_out", "startup_failure")

    def _emit(status: str) -> None:
        nonlocal last_status
        if status != last_status:
            out(f"[dsf] {status}")
            last_status = status

    while True:
        try:
            pr = _find_agent_pr(repo, issue_number)
            if pr is not None and pr["state"] == "CLOSED":
                out(f"[dsf] {repo}#{pr['number']} closed without merging; nothing to deploy.")
                return 0
            sha = _merged_commit_sha(repo, issue_number)
            if sha is None:
                _emit("waiting for the PR to merge before deploying...")
            else:
                conclusion = _deploy_run_conclusion(repo, sha)
                if conclusion == "success":
                    url = _read_live_url(product)
                    if url:
                        out(f"[dsf] deployed: {url}")
                        _record_deploy_url(product, url)
                        _comment_deploy_url(repo, issue_number, url)
                        return 0
                    _emit("deploy succeeded; waiting for the ingress URL...")
                elif conclusion in failed_conclusions:
                    out(
                        f"[dsf] deploy workflow {conclusion} for {sha[:7]}; see the "
                        "repo Actions tab, fix + re-merge, then `dsf charter url`."
                    )
                    return 1
                else:
                    _emit(f"deploying {sha[:7]}...")
        except transient as exc:
            _emit(f"transient GitHub/Azure error ({exc.__class__.__name__}); retrying...")

        if timeout is not None and clock() - start >= timeout:
            out(
                f"[dsf] still deploying after {int(timeout)}s; re-run "
                "`dsf charter watch --product <product>` or `dsf charter url "
                "--product <product>` to resume."
            )
            return 2
        sleep(poll_interval)
```

- [ ] **Step 5: Wire the deploy phase into `implement`**

In `_cmd_charter_implement`, replace the final `return _watch_and_request_review(...)`:

```python
    return _watch_and_request_review(
        repo_full,
        _issue_number_from_url(issue_url),
        timeout=_resolve_watch_timeout(args.timeout),
        poll_interval=_resolve_watch_poll_interval(args.poll_interval),
    )
```

with:

```python
    issue_no = _issue_number_from_url(issue_url)
    rc = _watch_and_request_review(
        repo_full,
        issue_no,
        timeout=_resolve_watch_timeout(args.timeout),
        poll_interval=_resolve_watch_poll_interval(args.poll_interval),
    )
    if rc != 0:
        return rc
    return _watch_deploy_and_surface_url(
        repo_full,
        product,
        issue_no,
        timeout=_resolve_watch_timeout(args.timeout),
        poll_interval=_resolve_watch_poll_interval(args.poll_interval),
    )
```

- [ ] **Step 6: Wire the deploy phase into `watch`**

In `_cmd_charter_watch`, replace the final `return _watch_and_request_review(...)`:

```python
    return _watch_and_request_review(
        repo_full,
        issue_number,
        timeout=_resolve_watch_timeout(args.timeout),
        poll_interval=_resolve_watch_poll_interval(args.poll_interval),
    )
```

with:

```python
    rc = _watch_and_request_review(
        repo_full,
        issue_number,
        timeout=_resolve_watch_timeout(args.timeout),
        poll_interval=_resolve_watch_poll_interval(args.poll_interval),
    )
    if rc != 0:
        return rc
    return _watch_deploy_and_surface_url(
        repo_full,
        product,
        issue_number,
        timeout=_resolve_watch_timeout(args.timeout),
        poll_interval=_resolve_watch_poll_interval(args.poll_interval),
    )
```

- [ ] **Step 7: Patch the deploy phase in existing command-level tests**

The command tests that stub `_watch_and_request_review` and invoke `main([...])` now also reach `_watch_deploy_and_surface_url`. In `cli/tests/cli/test_charter.py`, add a stub for the new function to each such test so it does not shell out. The affected tests (find each `monkeypatch.setattr("dsf.cli.charter._watch_and_request_review", ...)` that is followed by a `main([... "implement" ...])` or `main([... "watch" ...])` call) are:
- the `implement` happy-path test (~line 535),
- `test_watch_command_uses_explicit_issue` (~line 1022),
- `test_watch_command_finds_newest_handoff_issue` (~line 1033),
- the second `implement` test (~line 1385).

In each, immediately after the `_watch_and_request_review` `setattr`, add:

```python
    monkeypatch.setattr(
        "dsf.cli.charter._watch_deploy_and_surface_url",
        lambda *a, **k: 0,
    )
```

(The `--no-wait` test ~line 548 returns before watching, so it needs no change.)

- [ ] **Step 8: Run tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -q`
Expected: PASS (new deploy-watch tests pass; patched command tests pass).

- [ ] **Step 9: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -m "feat: watch merge->deploy and surface the product's live URL"
```

---

## Task 9: `dsf charter url` command

On-demand lookup of the product's live URL (live `az` read, then the recorded fallback).

**Files:**
- Modify: `cli/src/dsf/cli/charter.py` (`_cmd_charter_url`; register `url` in `add_charter_subcommands` ~line 1003-1012)
- Test: `cli/tests/cli/test_charter.py`

- [ ] **Step 1: Write the failing tests**

Add to `cli/tests/cli/test_charter.py`:

```python
def test_url_subcommand_parses():
    parser = build_parser()
    args = parser.parse_args(["charter", "url", "--product", "alpha"])
    assert args.command == "charter" and args.product == "alpha"


def test_url_prints_live_url(monkeypatch, capsys):
    monkeypatch.setattr("dsf.cli.charter._read_live_url", lambda p: "https://live.example.net")
    rc = main(["charter", "url", "--product", "alpha"])
    out = capsys.readouterr().out.strip()
    assert rc == 0
    assert out == "https://live.example.net"


def test_url_falls_back_to_recorded(monkeypatch, capsys):
    monkeypatch.setattr("dsf.cli.charter._read_live_url", lambda p: None)
    monkeypatch.setattr(
        "dsf.cli.charter._recorded_deploy_url", lambda p: "https://recorded.example.net"
    )
    rc = main(["charter", "url", "--product", "alpha"])
    out = capsys.readouterr().out.strip()
    assert rc == 0
    assert out == "https://recorded.example.net"


def test_url_errors_when_unknown(monkeypatch, capsys):
    monkeypatch.setattr("dsf.cli.charter._read_live_url", lambda p: None)
    monkeypatch.setattr("dsf.cli.charter._recorded_deploy_url", lambda p: None)
    rc = main(["charter", "url", "--product", "alpha"])
    err = capsys.readouterr().err
    assert rc == 1
    assert "no live URL" in err
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest cli/tests/cli/test_charter.py -k "url" -q`
Expected: FAIL (`url` subcommand not registered; `_cmd_charter_url` missing).

- [ ] **Step 3: Add the command handler**

In `cli/src/dsf/cli/charter.py`, add near the other `_cmd_charter_*` handlers:

```python
def _cmd_charter_url(args: argparse.Namespace) -> int:
    """Print the product's live deployment URL (live az read, then recorded fallback)."""
    product = args.product
    url = _read_live_url(product) or _recorded_deploy_url(product)
    if not url:
        print(
            f"[dsf] no live URL for {product} yet; the first deploy may still be "
            f"running (see `dsf charter watch --product {product}`).",
            file=sys.stderr,
        )
        return 1
    print(url)
    return 0
```

- [ ] **Step 4: Register the subcommand**

In `add_charter_subcommands`, before the trailing `for name, func, help_text in (...)` loop, add:

```python
    url_parser = charter_sub.add_parser(
        "url", help="print the product's live deployment URL"
    )
    url_parser.add_argument("--product", required=True, help="product key")
    url_parser.set_defaults(func=_cmd_charter_url)
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -k "url" -q`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -m "feat: add dsf charter url to print the product's live URL"
```

---

## Task 10: Offboard the GitHub `production` Environment

Best-effort teardown of the seeded delivery path's GitHub side: delete the repo's `production` Environment during `dsf offboard`. (The Azure side already goes with the resource group.)

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py` (`InstanceOffboarder.plan()` ~line 1251-1285; `InstanceOffboarder._execute_step` ~line 1310-1345; add a `_delete_github_environment` helper on that class)
- Test: `cli/tests/instance/test_offboard.py`

- [ ] **Step 1: Write the failing tests**

Add to `cli/tests/instance/test_offboard.py` (it already has `_seed_manifest`, `InstanceOffboarder`, and imports `subprocess`/`MagicMock`):

```python
def test_offboard_deletes_production_environment_when_present(tmp_path):
    _seed_manifest(tmp_path)
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return subprocess.CompletedProcess(cmd, 0, stdout="", stderr="")

    plan = InstanceOffboarder("demo", run=fake_run, repo_root=tmp_path).apply(execute=True)
    env_delete = next(
        c for c in calls
        if c[:4] == ["gh", "api", "--method", "DELETE"]
        and c[-1].endswith("/environments/production")
    )
    assert env_delete[-1] == "/repos/acme/demo/environments/production"
    step = next(s for s in plan.steps if s.name == "delete_github_environment")
    assert step.result == "removed"


def test_offboard_tolerates_absent_production_environment(tmp_path):
    _seed_manifest(tmp_path)

    def fake_run(cmd, **kwargs):
        if cmd[:4] == ["gh", "api", "--method", "DELETE"]:
            return subprocess.CompletedProcess(cmd, 1, stdout="", stderr="HTTP 404")
        return subprocess.CompletedProcess(cmd, 0, stdout="", stderr="")

    plan = InstanceOffboarder("demo", run=fake_run, repo_root=tmp_path).apply(execute=True)
    step = next(s for s in plan.steps if s.name == "delete_github_environment")
    assert step.result == "not-found (already absent)"
```

- [ ] **Step 2: Update the offboard step-order assertion**

In `test_offboard_plan_step_order_and_purge_default`, insert `"delete_github_environment"` between `"purge_soft_deleted"` and `"remove_runtime_index"`:

```python
        "purge_soft_deleted",
        "delete_github_environment",
        "remove_runtime_index",
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `uv run pytest cli/tests/instance/test_offboard.py -q`
Expected: FAIL (step missing; `delete_github_environment` not handled).

- [ ] **Step 4: Add the plan step**

In `InstanceOffboarder.plan()`, insert this `ProvisionStep` into the `steps` list immediately after `purge_step` and before the `remove_runtime_index` step:

```python
                ProvisionStep(
                    name="delete_github_environment",
                    description=(
                        f"Delete the 'production' GitHub Environment on "
                        f"{spec.github_repo()} (best-effort)"
                    ),
                ),
```

- [ ] **Step 5: Add the dispatch + helper**

In `InstanceOffboarder._execute_step`, add this branch immediately after the `purge_soft_deleted` branch and before `remove_runtime_index`:

```python
        elif step.name == "delete_github_environment":
            step.executed = True
            step.result = self._delete_github_environment()
```

Then add this method to `InstanceOffboarder`:

```python
    def _delete_github_environment(self) -> str:
        """Best-effort delete of the repo's 'production' GitHub Environment.

        Tolerates a missing repo/environment (already gone): a non-zero ``gh`` exit
        records ``"not-found (already absent)"`` rather than failing offboard, so a
        re-run after the repo is deleted still completes.
        """
        repo = self._load_manifest().spec.github_repo()
        proc = self._run(
            [
                "gh", "api", "--method", "DELETE",
                f"/repos/{repo}/environments/production",
            ],
            check=False, capture_output=True, text=True,
        )
        if getattr(proc, "returncode", 0) == 0:
            return "removed"
        return "not-found (already absent)"
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_offboard.py -q`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add cli/src/dsf/instance/provisioner.py cli/tests/instance/test_offboard.py
git commit -m "feat: delete the production GitHub Environment on offboard"
```

---

## Task 11: Documentation — ADR + creation page

Record the decision and document the deploy leg for operators.

**Files:**
- Create: `docs/adr/0022-product-deploy-leg-live-url.md`
- Modify: `docs/site/concept/creation.md` (append a section)

- [ ] **Step 1: Write the ADR**

Create `docs/adr/0022-product-deploy-leg-live-url.md`:

```markdown
# 0022. Creation-phase deploy leg: ship the product to a live URL

Status: Accepted

## Context

The Creation phase previously ended at merged pull requests: `dsf charter
implement` filed the bootstrap issue, the Copilot Coding Agent built the product,
and DSF requested review. Nothing deployed the result, so "high maturity" produced
a green repo, not a running product. The charter interview promises that, at
sufficient maturity, the end result is a live URL.

## Decision

Add a **deploy leg** to Creation, fully per-product and self-contained in
`rg-dsf-{product}`:

- **Contract (paved road).** The product ships as a container serving HTTP on port
  8080 via a root `Dockerfile`. This is stated in the bootstrap issue and made
  governing by constitution Principle VI (schema bumped to 2 so existing products
  re-render).
- **Hosting.** `infra/main.bicep` provisions a per-product container registry, an
  externally reachable Azure Container App (started on the public
  `containerapps-helloworld` image so the app + FQDN exist immediately; it 503s
  until the first real deploy), and a GitHub Actions **OIDC** deploy identity
  (user-assigned managed identity + federated credential scoped to
  `environment:production`). The app runs in its own dedicated managed environment,
  isolated from the factory orchestrator.
- **Deployer.** `dsf new` seeds `deploy.yml` into the product repo. On every merge
  to `main` it logs in with OIDC (non-secret repo variables), builds + pushes the
  image on the runner, `az containerapp update`s the app, and records the FQDN on
  a GitHub `production` Environment. Least privilege: the deploy identity holds
  AcrPush/AcrPull on the ACR and Contributor scoped to the one app.
- **Maturity.** The dial still gates the *merge* only (high = auto-merge, low =
  human approval); deploy runs on every merge, both maturities.
- **Tamper hardening.** A seeded `CODEOWNERS` assigns `.github/workflows/*` to the
  product owner, and the branch-protection ruleset requires code-owner review, so
  workflow edits need owner sign-off even under auto-merge.
- **Surfacing.** `dsf charter implement`/`watch` waits for merge→deploy and prints
  the URL, records it in the owner App Config index (`DSF_DEPLOY_URL`), and
  comments it on the bootstrap issue. `dsf charter url --product X` reads it on
  demand.

## Consequences

- A finished high-maturity product is reachable at a real URL, closing the
  charter's promise.
- Teardown stays simple: everything is in `rg-dsf-{product}`, so `dsf offboard`
  (resource-group delete) removes the delivery path with the rest; the one
  non-RG artifact, the `production` GitHub Environment, is best-effort deleted in
  the same offboard.
- The bootstrap image means a provisioned-but-never-deployed product returns 503
  until its first merge — acceptable and explicit (no fake app is served).
- Deploy uses keyless OIDC; no cloud credential is stored in the product repo.
```

- [ ] **Step 2: Append the creation-page section**

Append to the end of `docs/site/concept/creation.md`:

```markdown
## Live deployment (the deploy leg)

Creation does not stop at merged PRs. Every product repo is provisioned with a
delivery path so the finished app runs at a real URL.

**The contract.** The product ships as a container serving HTTP on port 8080 via a
`Dockerfile` at the repository root. The bootstrap issue states this, and the
product constitution makes it governing (Principle VI, "Deployable Web Service").

**What `dsf new` provisions** (all inside `rg-dsf-{product}`):

- a per-product container registry;
- an externally reachable Azure Container App, started on a public hello-world
  image so its URL exists immediately (it returns 503 until the first real deploy);
- a GitHub Actions OIDC deploy identity (managed identity + federated credential
  scoped to the `production` environment), with least-privilege roles (push/pull on
  the registry, Contributor on just the one app);
- a `deploy.yml` workflow plus a `CODEOWNERS` guarding `.github/workflows/*`.

**How deploys happen.** On every merge to `main`, `deploy.yml` logs in with OIDC
(non-secret repo variables), builds and pushes the image, updates the Container
App, and records the URL on the repo's `production` Environment. The maturity dial
gates only the *merge* (high auto-merges once `ci` is green; low needs a human);
deploy then runs unconditionally. Editing a workflow needs the product owner's
review even under auto-merge (CODEOWNERS).

**Seeing the URL.** `dsf charter implement` (and `dsf charter watch`) wait for the
merge and deploy, then print the live URL, record it, and comment it on the
bootstrap issue. Ask again any time with:

    dsf charter url --product <product>

Teardown removes it with everything else: `dsf offboard <product>` deletes
`rg-dsf-{product}`.
```

- [ ] **Step 3: Commit** (docs need no tests)

```bash
git add docs/adr/0022-product-deploy-leg-live-url.md docs/site/concept/creation.md
git commit -m "docs: record the creation-phase deploy leg (ADR 0022 + creation page)"
```

---

## Task 12: Full quality gate

- [ ] **Step 1: Run the whole test suite**

Run: `uv run pytest -q`
Expected: PASS (no failures; one pre-existing skip is fine).

- [ ] **Step 2: Lint everything**

Run: `uv run ruff check .`
Expected: `All checks passed!`
If ruff flags import order (rule `I`) in `provisioner.py`/`charter.py`, autofix with `uv run ruff check --fix .` and re-run.

- [ ] **Step 3: Import boundaries**

Run: `uv run lint-imports`
Expected: `Contracts: 4 kept, 0 broken`.

- [ ] **Step 4: Compile the bicep once more**

Run: `az bicep build --file infra/main.bicep --outfile /dev/null`
Expected: exit 0.

- [ ] **Step 5: Final commit (only if any autofix changed files)**

```bash
git add -A
git commit -m "chore: ruff autofix for the deploy leg"
```

---

## Notes for the implementer

- **`az`/`gh` never run in tests.** Every Azure/GitHub call goes through an
  injected `run`/helper that tests monkeypatch. Do not add live calls to test paths.
- **Dedicated env, per the design.** The product web app gets its own managed
  environment (`{namePrefix}-app-cae-{suffix}`), separate from the factory
  orchestrator's `{namePrefix}-cae-{suffix}`, for blast-radius isolation. The app
  name (`{namePrefix}-app`) never collides with the orchestrator
  (`{namePrefix}-orchestrator`).
- **`_read_live_url` derives the app name** as `f"{spec.name_prefix}-app"` — it must
  stay in lockstep with the bicep `productApp` name `'${namePrefix}-app'`. If you
  rename one, rename both.
- **Federated-credential subject** (`repo:{owner}/{repo}:environment:production`)
  must match the workflow's `environment: production` and the `production` GitHub
  Environment created by `configure_deploy`. All three say `production`.
- **Timeouts are per phase.** Review-watch and deploy-watch each get the resolved
  watch timeout; a resumable `rc=2` from either is surfaced to the operator.
```
