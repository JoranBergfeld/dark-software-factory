# SP2 — Per-Product Azure Provisioning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the SP1 `provision_azure` deferred stub into a real, idempotent step that creates a per-product Azure resource group, deploys `infra/main.bicep` into it, and captures the deployment outputs into the instance manifest.

**Architecture:** Extend `InstanceSpec` with Azure fields and a derived, randomized resource prefix (`make_name_prefix`). `InstanceProvisioner.plan()` un-defers Azure into two active `az` steps; `apply(execute=True)` runs them via the injectable runner and parses `az deployment group create --query properties.outputs` JSON into a new `AzureProvisionResult` stored on the manifest. The CLI gains a required `--name-prefix` plus Azure flags and reuses the persisted prefix for idempotency. Council and SRE stay deferred (SP3/SP5).

**Tech Stack:** Python 3.12, pydantic v2, argparse CLI (`python -m dsf.cli`), `az` CLI (shelled out), pytest, ruff. Spec: `docs/superpowers/specs/2026-06-17-dsf-per-product-azure-provisioning-design.md`.

---

## Task 1: Name-prefix derivation helper

**Files:**
- Create: `src/dsf/instance/naming.py`
- Modify: `src/dsf/instance/__init__.py`
- Test: `tests/instance/test_naming.py`

- [ ] **Step 1: Write the failing test**

Create `tests/instance/test_naming.py`:

```python
"""Tests for Azure name-prefix derivation."""

from __future__ import annotations

import pytest

from dsf.instance.naming import make_name_prefix


def test_make_name_prefix_appends_token_and_substrings():
    assert make_name_prefix("microproduct", token="wxyz") == "microprowxyz"


def test_make_name_prefix_sanitizes_to_lowercase_alnum():
    assert make_name_prefix("My-Cool_Product!", token="ab12") == "mycoolprab12"


def test_make_name_prefix_caps_total_length_at_12():
    out = make_name_prefix("averylongproductname", token="qrst")
    assert out == "averylonqrst"
    assert len(out) == 12


def test_make_name_prefix_random_token_starts_with_letter_and_varies():
    a = make_name_prefix("demo")
    b = make_name_prefix("demo")
    assert a[0].isalpha()
    assert a.startswith("demo")
    assert len(a) == 8
    assert a != b  # random 4-char token


def test_make_name_prefix_rejects_base_without_letters():
    with pytest.raises(ValueError):
        make_name_prefix("___")


def test_make_name_prefix_rejects_leading_digit():
    with pytest.raises(ValueError):
        make_name_prefix("1demo")
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/instance/test_naming.py -q`
Expected: FAIL — `ModuleNotFoundError: No module named 'dsf.instance.naming'`.

- [ ] **Step 3: Write minimal implementation**

Create `src/dsf/instance/naming.py`:

```python
"""Derive Azure-safe resource name prefixes for an instance.

Azure's ``namePrefix`` Bicep parameter is 3-12 lowercase characters and must start
with a letter (Key Vault and similar resources reject leading digits). We sanitize
the user-supplied base, substring it, and append a short random token so re-created
instances never collide with a prior deployment's globally-unique names — and dodge
Key Vault's 90-day soft-delete name reservation.
"""

from __future__ import annotations

import re
import secrets

_ALPHABET = "abcdefghijklmnopqrstuvwxyz0123456789"
_MAX_LEN = 12
_VALID = re.compile(r"^[a-z][a-z0-9]{2,11}$")


def _random_token(length: int) -> str:
    """Return a random lowercase-alphanumeric token of the given length."""
    return "".join(secrets.choice(_ALPHABET) for _ in range(length))


def make_name_prefix(base: str, *, token: str | None = None, token_len: int = 4) -> str:
    """Return an Azure-safe effective name prefix derived from ``base``.

    ``base`` is lowercased and stripped to alphanumerics, must start with a letter,
    is substringed to leave room for a ``token_len``-char token, and the token
    (random by default; inject ``token`` for deterministic tests) is appended.
    Raises ``ValueError`` if the base or derived prefix is invalid.
    """
    cleaned = "".join(c for c in base.lower() if c.isalnum())
    if not cleaned or not cleaned[0].isalpha():
        raise ValueError(f"name prefix base must start with a letter: {base!r}")
    stem = cleaned[: _MAX_LEN - token_len]
    tok = token if token is not None else _random_token(token_len)
    prefix = f"{stem}{tok}"
    if not _VALID.match(prefix):
        raise ValueError(f"derived name prefix is invalid: {prefix!r}")
    return prefix
```

- [ ] **Step 4: Export from the package**

In `src/dsf/instance/__init__.py`, add the import and `__all__` entry. The file becomes:

```python
"""Instance provisioning — turn an InstanceSpec into a product factory instance."""

from dsf.instance.naming import make_name_prefix
from dsf.instance.provisioner import InstanceProvisioner
from dsf.instance.spec import (
    InstanceManifest,
    InstancePlan,
    InstanceSpec,
    ProvisionStep,
    default_label_taxonomy,
    instances_dir,
    manifest_path,
    read_manifest,
    write_manifest,
)

__all__ = [
    "InstanceManifest",
    "InstancePlan",
    "InstanceProvisioner",
    "InstanceSpec",
    "ProvisionStep",
    "default_label_taxonomy",
    "instances_dir",
    "make_name_prefix",
    "manifest_path",
    "read_manifest",
    "write_manifest",
]
```

- [ ] **Step 5: Run test to verify it passes**

Run: `uv run pytest tests/instance/test_naming.py -q && uv run ruff check src/dsf/instance/naming.py tests/instance/test_naming.py`
Expected: PASS (6 passed); ruff clean.

- [ ] **Step 6: Commit**

```bash
git add src/dsf/instance/naming.py src/dsf/instance/__init__.py tests/instance/test_naming.py
git -c commit.gpgsign=false commit -m "feat(instance): add Azure name-prefix derivation helper

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 2: Extend InstanceSpec + add AzureProvisionResult

**Files:**
- Modify: `src/dsf/instance/spec.py`
- Modify: `src/dsf/instance/__init__.py`
- Test: `tests/instance/test_spec.py` (append)

- [ ] **Step 1: Write the failing test**

In `tests/instance/test_spec.py`, replace the existing import block (lines 5-15) with this (adds `AzureProvisionResult`, `pytest`, `ValidationError`):

```python
import pytest
from pydantic import ValidationError

from dsf.instance.spec import (
    AzureProvisionResult,
    InstanceManifest,
    InstancePlan,
    InstanceSpec,
    ProvisionStep,
    default_label_taxonomy,
    instances_dir,
    manifest_path,
    read_manifest,
    write_manifest,
)
```

Then append these tests to the end of the file:

```python
def test_instance_spec_azure_defaults():
    spec = InstanceSpec(product="demo", owner="acme")
    assert spec.name_prefix == "dsf"
    assert spec.environment == "dev"
    assert spec.location == "swedencentral"
    assert spec.workload_principal_id == ""
    assert spec.deployment_name() == "dsf-demo"


def test_instance_spec_rejects_bad_name_prefix():
    with pytest.raises(ValidationError):
        InstanceSpec(product="demo", owner="acme", name_prefix="1bad")


def test_azure_provision_result_round_trips_in_manifest(tmp_path):
    spec = InstanceSpec(product="demo", owner="acme")
    plan = InstancePlan(product="demo", steps=[])
    azure = AzureProvisionResult(
        resource_group="rg-dsf-demo",
        deployment_name="dsf-demo",
        location="swedencentral",
        outputs={"cosmosEndpoint": "https://x"},
    )
    manifest = InstanceManifest(spec=spec, plan=plan, executed=True, azure=azure)
    write_manifest(manifest, repo_root=tmp_path)

    loaded = read_manifest("demo", repo_root=tmp_path)
    assert loaded.azure is not None
    assert loaded.azure.outputs["cosmosEndpoint"] == "https://x"
    assert loaded.azure.deployment_name == "dsf-demo"
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/instance/test_spec.py -q`
Expected: FAIL — `ImportError: cannot import name 'AzureProvisionResult'`.

- [ ] **Step 3: Write minimal implementation**

In `src/dsf/instance/spec.py`, change the pydantic import line (line 13) from:

```python
from pydantic import BaseModel, Field
```

to:

```python
import re

from pydantic import BaseModel, Field, field_validator
```

Add the four new fields to `InstanceSpec` (immediately after the `label_taxonomy` field at line 34):

```python
    name_prefix: str = "dsf"
    environment: str = "dev"
    location: str = "swedencentral"
    workload_principal_id: str = ""
```

Add a validator and a `deployment_name()` method to `InstanceSpec` (after the `resource_group()` method, around line 46):

```python
    @field_validator("name_prefix")
    @classmethod
    def _validate_name_prefix(cls, v: str) -> str:
        if not re.match(r"^[a-z][a-z0-9]{2,11}$", v):
            raise ValueError(f"name_prefix must match ^[a-z][a-z0-9]{{2,11}}$: {v!r}")
        return v

    def deployment_name(self) -> str:
        """Deterministic ARM deployment name for this instance."""
        return f"dsf-{self.product}"
```

Add the `AzureProvisionResult` model immediately before `InstanceManifest` (around line 68):

```python
class AzureProvisionResult(BaseModel):
    """Captured result of the per-product Azure deployment."""

    resource_group: str
    deployment_name: str
    location: str
    outputs: dict[str, str] = Field(default_factory=dict)
```

Add the `azure` field to `InstanceManifest` (after the `executed` field, around line 73):

```python
    azure: AzureProvisionResult | None = None
```

- [ ] **Step 4: Export AzureProvisionResult from the package**

In `src/dsf/instance/__init__.py`, add `AzureProvisionResult` to both the `from dsf.instance.spec import (...)` block and `__all__` (keep alphabetical-ish order; place `AzureProvisionResult` right after the spec import opens and as the first `__all__` entry):

```python
from dsf.instance.spec import (
    AzureProvisionResult,
    InstanceManifest,
    InstancePlan,
    InstanceSpec,
    ProvisionStep,
    default_label_taxonomy,
    instances_dir,
    manifest_path,
    read_manifest,
    write_manifest,
)
```

and in `__all__` add `"AzureProvisionResult",` as the first entry.

- [ ] **Step 5: Run test to verify it passes**

Run: `uv run pytest tests/instance/test_spec.py -q && uv run ruff check src/dsf/instance tests/instance/test_spec.py`
Expected: PASS (9 passed); ruff clean.

- [ ] **Step 6: Commit**

```bash
git add src/dsf/instance/spec.py src/dsf/instance/__init__.py tests/instance/test_spec.py
git -c commit.gpgsign=false commit -m "feat(instance): add Azure fields to InstanceSpec + AzureProvisionResult

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 3: Provisioner plan() — un-defer Azure into two active steps

**Files:**
- Modify: `src/dsf/instance/provisioner.py`
- Test: `tests/instance/test_provisioner.py`

- [ ] **Step 1: Update the plan tests (and the two apply assertions the plan change affects)**

In `tests/instance/test_provisioner.py`, replace `test_plan_step_order_and_names` (lines 15-26) and `test_plan_deferred_flags` (lines 29-32) with:

```python
def test_plan_step_order_and_names():
    plan = InstanceProvisioner(_spec()).plan()
    assert plan.product == "demo"
    assert [s.name for s in plan.steps] == [
        "create_repo",
        "squad_init",
        "squad_copilot",
        "create_resource_group",
        "provision_azure",
        "deploy_council",
        "deploy_sre",
        "write_config",
    ]


def test_plan_deferred_flags():
    plan = InstanceProvisioner(_spec()).plan()
    deferred = {s.name for s in plan.steps if s.deferred}
    assert deferred == {"deploy_council", "deploy_sre"}


def test_plan_create_resource_group_command():
    plan = InstanceProvisioner(_spec()).plan()
    rg = next(s for s in plan.steps if s.name == "create_resource_group")
    assert rg.command == [
        "az", "group", "create",
        "--name", "rg-dsf-demo", "--location", "swedencentral",
    ]


def test_plan_provision_azure_command_shape():
    plan = InstanceProvisioner(_spec()).plan()
    az = next(s for s in plan.steps if s.name == "provision_azure")
    assert az.command[:4] == ["az", "deployment", "group", "create"]
    assert az.command[az.command.index("-g") + 1] == "rg-dsf-demo"
    assert az.command[az.command.index("-n") + 1] == "dsf-demo"
    assert az.command[az.command.index("-f") + 1].endswith("infra/main.bicep")
    assert "namePrefix=dsf" in az.command
    assert "environmentName=dev" in az.command
    assert "location=swedencentral" in az.command
    assert "workloadPrincipalId=" in az.command
    assert az.command[az.command.index("--query") + 1] == "properties.outputs"
    assert az.command[-2:] == ["-o", "json"]
```

Then update the two apply tests the plan change affects:

In `test_apply_dry_run_writes_manifest_and_runs_nothing`, change the line
`assert results["provision_azure"] == "deferred"` to:

```python
    assert results["provision_azure"] == "dry-run"
    assert results["deploy_council"] == "deferred"
```

In `test_apply_execute_runs_real_steps_and_stubs_deferred`, replace the line
`assert not any(cmd[0] == "az" for cmd, _ in calls)` with:

```python
    # azure now provisions for real (RG + Bicep deployment):
    assert [
        "az", "group", "create", "--name", "rg-dsf-demo", "--location", "swedencentral",
    ] in executed
    assert any(cmd[:4] == ["az", "deployment", "group", "create"] for cmd in executed)
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest tests/instance/test_provisioner.py -q`
Expected: FAIL — the new `create_resource_group` step is absent and `provision_azure` is still deferred.

- [ ] **Step 3: Write the implementation**

In `src/dsf/instance/provisioner.py`, change the spec import block (lines 15-22) to also import `_default_repo_root`:

```python
from dsf.instance.spec import (
    InstanceManifest,
    InstancePlan,
    InstanceSpec,
    ProvisionStep,
    _default_repo_root,
    manifest_path,
    write_manifest,
)
```

Then, in `plan()`, replace the single `provision_azure` step (lines 76-88) with a `bicep` local plus two steps. Specifically, change the start of the `steps = [` body so that after the `squad_copilot` step you have:

```python
        repo_dir = s.resolved_repo()
        bicep = str((self._repo_root or _default_repo_root()) / "infra" / "main.bicep")
        steps = [
            ProvisionStep(
                name="create_repo",
                description=f"Create GitHub repo {s.github_repo()} ({s.visibility})",
                command=[
                    "gh", "repo", "create", s.github_repo(),
                    f"--{s.visibility}", "--clone",
                ],
            ),
            ProvisionStep(
                name="squad_init",
                description=f"Initialize Coding Squad in {s.github_repo()}",
                command=["squad", "init", "--preset", "default"],
                cwd=repo_dir,
            ),
            ProvisionStep(
                name="squad_copilot",
                description="Enable Copilot coding agent auto-assignment",
                command=["squad", "copilot", "--auto-assign"],
                cwd=repo_dir,
            ),
            ProvisionStep(
                name="create_resource_group",
                description=f"Create dedicated Azure resource group {s.resource_group()}",
                command=[
                    "az", "group", "create",
                    "--name", s.resource_group(),
                    "--location", s.location,
                ],
            ),
            ProvisionStep(
                name="provision_azure",
                description=(
                    f"Deploy backing services into {s.resource_group()} from infra/main.bicep"
                ),
                command=[
                    "az", "deployment", "group", "create",
                    "-g", s.resource_group(),
                    "-n", s.deployment_name(),
                    "-f", bicep,
                    "-p",
                    f"namePrefix={s.name_prefix}",
                    f"environmentName={s.environment}",
                    f"location={s.location}",
                    f"workloadPrincipalId={s.workload_principal_id}",
                    "--query", "properties.outputs", "-o", "json",
                ],
            ),
            ProvisionStep(
                name="deploy_council",
                description=(
                    f"Deploy feature-council runtime scoped to {s.product} (deferred to SP3)"
                ),
                deferred=True,
            ),
            ProvisionStep(
                name="deploy_sre",
                description=f"Deploy SRE agent for {s.product} (deferred to SP5)",
                deferred=True,
            ),
            ProvisionStep(
                name="write_config",
                description=f"Write instance manifest to config/instances/{s.product}.json",
            ),
        ]
```

(The net change: drop the old `repo_dir = s.resolved_repo()` line at the top of the method since it now lives just above `bicep`; remove the old deferred `provision_azure` block; insert the `create_resource_group` + new `provision_azure` steps.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest tests/instance/test_provisioner.py -q && uv run ruff check src/dsf/instance/provisioner.py tests/instance/test_provisioner.py`
Expected: PASS; ruff clean.

- [ ] **Step 5: Commit**

```bash
git add src/dsf/instance/provisioner.py tests/instance/test_provisioner.py
git -c commit.gpgsign=false commit -m "feat(instance): un-defer Azure into create-rg + bicep-deploy plan steps

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 4: Provisioner apply() — capture Azure deployment outputs

**Files:**
- Modify: `src/dsf/instance/provisioner.py`
- Test: `tests/instance/test_provisioner.py` (append)

- [ ] **Step 1: Write the failing tests**

Append to `tests/instance/test_provisioner.py`:

```python
def test_apply_execute_captures_azure_outputs(tmp_path):
    outputs_json = (
        '{"cosmosEndpoint": {"type": "String", "value": "https://demo.documents.azure.com"},'
        ' "keyVaultUri": {"type": "String", "value": "https://demovault.vault.azure.net"}}'
    )

    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        if cmd[:4] == ["az", "deployment", "group", "create"]:
            return MagicMock(returncode=0, stdout=outputs_json)
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    assert manifest.azure is not None
    assert manifest.azure.resource_group == "rg-dsf-demo"
    assert manifest.azure.deployment_name == "dsf-demo"
    assert manifest.azure.location == "swedencentral"
    assert manifest.azure.outputs["cosmosEndpoint"] == "https://demo.documents.azure.com"
    assert manifest.azure.outputs["keyVaultUri"] == "https://demovault.vault.azure.net"
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["create_resource_group"] == "executed"
    assert results["provision_azure"] == "executed"


def test_apply_dry_run_leaves_azure_unset(tmp_path):
    def fake_run(cmd, **kwargs):
        return MagicMock(returncode=0)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=False)

    assert manifest.azure is None
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["create_resource_group"] == "dry-run"
    assert results["provision_azure"] == "dry-run"
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest tests/instance/test_provisioner.py::test_apply_execute_captures_azure_outputs -q`
Expected: FAIL — `manifest.azure` is `None` (outputs are not captured yet).

- [ ] **Step 3: Write the implementation**

In `src/dsf/instance/provisioner.py`, add `import json` at the top (after `import subprocess`, line 10):

```python
import json
import subprocess
```

Add `AzureProvisionResult` to the spec import block (alongside the others):

```python
from dsf.instance.spec import (
    AzureProvisionResult,
    InstanceManifest,
    InstancePlan,
    InstanceSpec,
    ProvisionStep,
    _default_repo_root,
    manifest_path,
    write_manifest,
)
```

In `apply()`, initialize a result holder before the loop. Change:

```python
        plan = self.plan()
        for step in plan.steps:
```

to:

```python
        plan = self.plan()
        azure_result: AzureProvisionResult | None = None
        for step in plan.steps:
```

Add a `provision_azure` branch immediately before the final `else:` of the per-step
dispatch (i.e. between the `create_repo` clone branch ending at `step.result = "cloned"`
and the `else:`):

```python
            elif step.name == "provision_azure":
                proc = self._run(step.command, check=True, capture_output=True, text=True)
                azure_result = self._azure_result(proc)
                step.executed, step.result = True, "executed"
```

Change the manifest construction to pass `azure`:

```python
        manifest = InstanceManifest(
            spec=self.spec, plan=plan, executed=execute, azure=azure_result
        )
```

Add the `_azure_result` helper method (after `_repo_exists`, at the end of the class):

```python
    def _azure_result(self, proc: Any) -> AzureProvisionResult:
        """Parse ``az deployment group create --query properties.outputs`` JSON."""
        raw = getattr(proc, "stdout", None)
        parsed = json.loads(raw) if isinstance(raw, str) and raw.strip() else {}
        if not isinstance(parsed, dict):
            parsed = {}
        outputs = {
            k: (v.get("value") if isinstance(v, dict) else v) for k, v in parsed.items()
        }
        return AzureProvisionResult(
            resource_group=self.spec.resource_group(),
            deployment_name=self.spec.deployment_name(),
            location=self.spec.location,
            outputs={k: str(val) for k, val in outputs.items() if val is not None},
        )
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest tests/instance/test_provisioner.py -q && uv run ruff check src/dsf/instance/provisioner.py tests/instance/test_provisioner.py`
Expected: PASS (all provisioner tests); ruff clean.

- [ ] **Step 5: Commit**

```bash
git add src/dsf/instance/provisioner.py tests/instance/test_provisioner.py
git -c commit.gpgsign=false commit -m "feat(instance): capture Azure deployment outputs into the manifest

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 5: CLI `new` — Azure flags + idempotent prefix reuse

**Files:**
- Modify: `src/dsf/cli.py`
- Test: `tests/instance/test_cli_new.py`

- [ ] **Step 1: Update + add the failing tests**

Replace the entire contents of `tests/instance/test_cli_new.py` with:

```python
"""Tests for the `dsf new` CLI subcommand."""

from __future__ import annotations

import pytest

from dsf.cli import build_parser, main
from dsf.instance.spec import read_manifest


def test_new_parser_wiring():
    args = build_parser().parse_args(
        ["new", "--product", "demo", "--owner", "acme", "--name-prefix", "demopfx"]
    )
    assert args.command == "new"
    assert args.product == "demo"
    assert args.owner == "acme"
    assert args.name_prefix == "demopfx"
    assert args.environment == "dev"
    assert args.location == "swedencentral"
    assert args.execute is False
    assert args.write_plan is False


def test_new_requires_name_prefix():
    with pytest.raises(SystemExit):
        build_parser().parse_args(["new", "--product", "demo", "--owner", "acme"])


def test_new_dry_run_prints_plan_without_side_effects(capsys, tmp_path):
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "demopfx", "--config-root", str(tmp_path),
    ])
    out = capsys.readouterr().out
    assert rc == 0
    assert "create_repo" in out
    assert "provision_azure" in out
    assert "deferred" in out
    # pure preview: no manifest written even though a config root was provided
    assert not (tmp_path / "config" / "instances" / "demo.json").exists()


def test_new_write_plan_writes_manifest(tmp_path):
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "demopfx", "--write-plan", "--config-root", str(tmp_path),
    ])
    assert rc == 0
    assert (tmp_path / "config" / "instances" / "demo.json").exists()


def test_new_effective_prefix_is_stable_across_runs(tmp_path):
    argv = [
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "acmebase", "--write-plan", "--config-root", str(tmp_path),
    ]
    assert main(argv) == 0
    first = read_manifest("demo", repo_root=tmp_path).spec.name_prefix
    assert main(argv) == 0
    second = read_manifest("demo", repo_root=tmp_path).spec.name_prefix
    assert first == second  # reused, not regenerated
    assert first.startswith("acmebase")
    assert len(first) == 12
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest tests/instance/test_cli_new.py -q`
Expected: FAIL — `--name-prefix` is not a recognized argument yet (and `test_new_requires_name_prefix` is the inverse).

- [ ] **Step 3: Write the implementation**

In `src/dsf/cli.py`, replace the body of `_cmd_new` (lines 119-140) with:

```python
def _cmd_new(args: argparse.Namespace) -> int:
    """Create (or preview) a new isolated product factory instance."""
    from dsf.instance.naming import make_name_prefix
    from dsf.instance.provisioner import InstanceProvisioner
    from dsf.instance.spec import InstanceSpec, manifest_path, read_manifest

    root = Path(args.config_root) if args.config_root else None
    # Idempotent effective prefix: reuse the persisted one if the instance exists,
    # otherwise derive a fresh randomized prefix from the supplied base.
    if manifest_path(args.product, root).exists():
        name_prefix = read_manifest(args.product, repo_root=root).spec.name_prefix
    else:
        name_prefix = make_name_prefix(args.name_prefix)

    spec = InstanceSpec(
        product=args.product,
        owner=args.owner,
        repo=args.repo or "",
        visibility=args.visibility,
        runtime_target=args.runtime_target,
        name_prefix=name_prefix,
        environment=args.environment,
        location=args.location,
        workload_principal_id=args.workload_principal_id,
    )
    prov = InstanceProvisioner(spec, repo_root=root)
    if args.execute:
        plan = prov.apply(execute=True).plan
    elif args.write_plan:
        plan = prov.apply(execute=False).plan
    else:
        plan = prov.plan()
    _print_plan(plan, execute=args.execute)
    return 0
```

Then add the new arguments to the `new` subparser. After the existing
`p_new.add_argument("--runtime-target", ...)` block (lines 188-191) and before the
`--execute` argument, insert:

```python
    p_new.add_argument(
        "--name-prefix", required=True,
        help="base Azure resource name prefix (sanitized + randomized to <=12 lowercase chars)",
    )
    p_new.add_argument(
        "--environment", default="dev",
        help="Azure environment moniker (Bicep environmentName)",
    )
    p_new.add_argument(
        "--location", default="swedencentral",
        help="Azure region for the resource group and resources",
    )
    p_new.add_argument(
        "--workload-principal-id", default="",
        help="object id granted data-plane roles (empty = provision-only)",
    )
```

Also update the `--execute` help text (it no longer leaves Azure deferred):

```python
    p_new.add_argument(
        "--execute", action="store_true",
        help="run executable steps (gh/squad/az); council/SRE remain deferred",
    )
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest tests/instance/test_cli_new.py -q && uv run ruff check src/dsf/cli.py tests/instance/test_cli_new.py`
Expected: PASS (5 passed); ruff clean.

- [ ] **Step 5: Commit**

```bash
git add src/dsf/cli.py tests/instance/test_cli_new.py
git -c commit.gpgsign=false commit -m "feat(cli): add Azure flags + idempotent name-prefix to 'dsf new'

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 6: Docs, Make target, and full verification

**Files:**
- Modify: `Makefile`
- Modify: `README.md`
- Modify: `docs/RUNBOOK.md`

- [ ] **Step 1: Update the Make demo target**

In `Makefile`, change the `new-demo` target to supply the now-required prefix:

```makefile
new-demo:
	uv run python -m dsf.cli new --product demo --owner your-org --name-prefix demo
```

- [ ] **Step 2: Update the README layout note**

In `README.md`, replace the `instance/` sentence (under `## Layout`) with:

```
`instance/` — instance spec + provisioner powering the `dsf new` CLI (greenfield
product-factory scaffolding; creates the product repo + Coding Squad and provisions
a dedicated per-product Azure resource group from `infra/main.bicep`; council/SRE
deployment deferred to later sub-projects).
```

- [ ] **Step 3: Update the RUNBOOK section**

In `docs/RUNBOOK.md`, replace the body of the `## Creating a product instance (SP1)`
section (the prose line plus the `bash` block) with:

```markdown
## Creating a product instance (SP1 + SP2)

`dsf new` scaffolds an isolated product factory. `--name-prefix` is **required**;
it is sanitized and randomized into a <=12-char Azure resource prefix (persisted in
the manifest and reused on re-runs). Under `--execute`, repo creation + Coding Squad
init **and the dedicated Azure resource group + Bicep deployment** are real; feature
council and SRE deployment remain **deferred** stub steps (SP3/SP5).

```bash
# Preview the plan (no side effects):
uv run python -m dsf.cli new --product microbi --owner your-org --name-prefix microbi

# Preview AND write the instance manifest to config/instances/microbi.json:
uv run python -m dsf.cli new --product microbi --owner your-org --name-prefix microbi --write-plan

# Execute: create repo + init Squad + provision Azure (needs gh, @bradygaster/squad-cli, az):
uv run python -m dsf.cli new --product microbi --owner your-org --name-prefix microbi --execute
```
```

- [ ] **Step 4: Verify the whole suite, lint, and dry-run smoke**

Run:

```bash
uv run ruff check .
uv run pytest -q
uv run python -m dsf.cli new --product microbi --owner your-org --name-prefix microbi
```

Expected: ruff clean; full suite passes (SP1 baseline + the new SP2 tests, ~277 total);
the printed plan shows active `create_resource_group` + `provision_azure` steps with
their `az` commands and `deploy_council`/`deploy_sre` marked deferred. Confirm no
`config/instances/` directory is created by the pure-preview run (`git status`).

- [ ] **Step 5: Commit**

```bash
git add Makefile README.md docs/RUNBOOK.md
git -c commit.gpgsign=false commit -m "docs(sp2): document per-product Azure provisioning in README/RUNBOOK

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Self-Review

**Spec coverage:**
- Locked decision 1 (raw `az`, capture outputs) → Tasks 3 (commands) + 4 (capture). ✓
- Locked decision 2 (single-stage `--execute`) → Task 4 apply executes az for real; dry-run leaves `azure=None`. ✓
- Locked decision 3 (required `--name-prefix`, sanitize+substring+random, persisted/reused) → Task 1 (`make_name_prefix`), Task 5 (required flag + manifest reuse). ✓
- `AzureProvisionResult` + `InstanceManifest.azure` → Task 2. ✓
- Out-of-scope items (build_services('azure'), role wiring, what-if, destroy) → untouched. ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code. ✓

**Type consistency:**
- `make_name_prefix(base, *, token=None, token_len=4) -> str` — defined Task 1, called Task 5. ✓
- `InstanceSpec.name_prefix/environment/location/workload_principal_id` + `deployment_name()` — defined Task 2, used Tasks 3/4/5. ✓
- `AzureProvisionResult(resource_group, deployment_name, location, outputs)` — defined Task 2, built Task 4, asserted Tasks 2/4. ✓
- Step names `create_resource_group` / `provision_azure` — consistent across Tasks 3/4 tests + impl. ✓
- `_azure_result(self, proc)` — defined + used Task 4; `Any` already imported in provisioner. ✓

**Test impact on SP1:** Task 3 updates the two SP1 apply assertions affected by un-deferring Azure; Task 5 rewrites the SP1 CLI tests to pass `--name-prefix`. Both are called out explicitly. The clone-idempotency tests are unaffected (their `fake_run` returns `MagicMock`, and `_azure_result` tolerates non-string stdout). ✓
