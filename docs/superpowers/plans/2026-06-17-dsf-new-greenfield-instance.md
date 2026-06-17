# SP1 — `dsf new` Greenfield Instance (Walking Skeleton) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `dsf new <product>` CLI command that produces an isolated product-factory *instance shell* — an ordered provisioning plan that (on `--execute`) creates the product GitHub repo and initializes the Coding Squad, writes a per-product instance manifest, and represents Azure/council/SRE wiring as deferred stub steps.

**Architecture:** A small `dsf.instance` package: `spec.py` holds the pydantic data models (`InstanceSpec`, `ProvisionStep`, `InstancePlan`, `InstanceManifest`) plus manifest read/write IO; `provisioner.py` holds `InstanceProvisioner` which builds the ordered plan and applies it via an **injectable subprocess runner** (same pattern as `dsf.github_client.RealGitHubClient`). The CLI gains a `new` subcommand. Everything runs offline and dry-run by default; `--execute` shells out to `gh`/`squad`; Azure/council/SRE steps are marked `deferred` (filled in by SP2/SP3/SP5).

**Tech Stack:** Python 3.12, `uv`, `pydantic` v2, `argparse`, `pytest` (`asyncio_mode=auto`), `ruff` (line-length 100, rules E/F/I/UP/B). External CLIs `gh` and `@bradygaster/squad-cli` are only invoked under `--execute` and are always behind the injectable runner in tests.

---

## Conventions recap (read before starting)

- pydantic v2 models, every module starts with `from __future__ import annotations`.
- Shell out via an injectable callable defaulting to `subprocess.run` (see `src/dsf/github_client.py:30-36` and its tests `tests/test_github_client.py:28-33`). Tests pass a `fake_run` that records calls — **no real network/CLI in tests**.
- Tests mirror `src/` under `tests/` (e.g. `src/dsf/instance/spec.py` → `tests/instance/test_spec.py`).
- Config-as-data lives under `config/`; loaders live under `src/dsf/config/` (see `src/dsf/config/registry.py`). SP1 writes per-instance manifests to `config/instances/<product>.json` — deliberately separate from the legacy multi-product `config/products.json`, because each instance is isolated to one product.
- CLI is argparse subcommands invoked as `python -m dsf.cli <cmd>` (see `src/dsf/cli.py:108-145`). No console-script entry point — keep it that way.
- Run tests with `uv run pytest <path> -v` and lint with `uv run ruff check .`.

## File structure (locked)

```
src/dsf/instance/
  __init__.py          # re-exports: InstanceSpec, ProvisionStep, InstancePlan,
                       #   InstanceManifest, InstanceProvisioner, manifest IO
  spec.py              # data models + manifest read/write IO (no side effects beyond file IO)
  provisioner.py       # InstanceProvisioner: plan() + apply(execute=...) via injectable runner
src/dsf/cli.py         # MODIFY: add `new` subcommand (_cmd_new, _print_plan, parser wiring)
config/instances/      # NEW dir (created on first manifest write; holds <product>.json)
tests/instance/
  __init__.py
  test_spec.py         # spec defaults + manifest round-trip
  test_provisioner.py  # plan() shape + apply() dry-run/execute/idempotency
  test_cli_new.py      # parser wiring + dry-run print + --write-plan file write
Makefile               # MODIFY: add `new-demo` dry-run convenience target
README.md              # MODIFY: layout note for instance/ + `dsf new`
docs/RUNBOOK.md        # MODIFY: "Creating a product instance (SP1)" subsection
```

## Coverage map (SP1 spec → tasks)

- Instance contract/manifest → Tasks 1, 2.
- Create product repo + `squad init` → Tasks 3, 4 (plan + execute).
- Per-product factory config (manifest write) → Tasks 2, 4.
- Dry-run stubs for Azure/council/SRE → Task 3 (`deferred=True` steps).
- CLI `dsf new` end-to-end shell → Task 5.
- Docs/Make/verify → Task 6.

---

## Task 1: Instance spec + label-taxonomy defaults

**Files:**
- Create: `src/dsf/instance/__init__.py`
- Create: `src/dsf/instance/spec.py`
- Create: `tests/instance/__init__.py`
- Test: `tests/instance/test_spec.py`

- [ ] **Step 1: Write the failing test**

Create `tests/instance/__init__.py` (empty file) and `tests/instance/test_spec.py`:

```python
"""Tests for instance spec models and defaults."""

from __future__ import annotations

from dsf.instance.spec import InstanceSpec, default_label_taxonomy


def test_default_label_taxonomy_shape():
    tax = default_label_taxonomy()
    assert set(tax) == {"type", "area", "severity"}
    assert "feature" in tax["type"]
    assert "sev-critical" in tax["severity"]


def test_instance_spec_defaults():
    spec = InstanceSpec(product="demo", owner="acme")
    assert spec.visibility == "private"
    assert spec.runtime_target == "homelab"
    assert spec.confidence_threshold == 0.6
    assert spec.label_taxonomy == default_label_taxonomy()


def test_instance_spec_derivations():
    spec = InstanceSpec(product="demo", owner="acme")
    assert spec.resolved_repo() == "demo"
    assert spec.github_repo() == "acme/demo"
    assert spec.resource_group() == "rg-dsf-demo"


def test_instance_spec_explicit_repo_override():
    spec = InstanceSpec(product="demo", owner="acme", repo="demo-app")
    assert spec.resolved_repo() == "demo-app"
    assert spec.github_repo() == "acme/demo-app"
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/instance/test_spec.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'dsf.instance'`.

- [ ] **Step 3: Write minimal implementation**

Create `src/dsf/instance/__init__.py` with **only** the names that exist after this task (it grows in Tasks 2 and 3 — importing names that don't yet exist would break every import of the package):

```python
"""Instance provisioning — turn an InstanceSpec into a product factory instance."""

from dsf.instance.spec import InstanceSpec, default_label_taxonomy

__all__ = ["InstanceSpec", "default_label_taxonomy"]
```

Create `src/dsf/instance/spec.py` (models only for now — the IO helpers and the rest are added in Task 2):

```python
"""Instance spec + manifest models and on-disk IO.

An :class:`InstanceSpec` is the *desired state* for one isolated product
factory. A :class:`ProvisionStep` is a single ordered action; an
:class:`InstancePlan` is the full ordered sequence; an
:class:`InstanceManifest` is the persisted record (spec + plan + status).
"""

from __future__ import annotations

from pydantic import BaseModel, Field


def default_label_taxonomy() -> dict[str, list[str]]:
    """Default GitHub label taxonomy applied to a new product."""
    return {
        "type": ["feature", "bug", "chore"],
        "area": ["api", "ui", "infra"],
        "severity": ["sev-low", "sev-medium", "sev-high", "sev-critical"],
    }


class InstanceSpec(BaseModel):
    """Desired state for one isolated product factory instance."""

    product: str
    owner: str
    repo: str = ""
    visibility: str = "private"
    runtime_target: str = "homelab"
    confidence_threshold: float = 0.6
    label_taxonomy: dict[str, list[str]] = Field(default_factory=default_label_taxonomy)

    def resolved_repo(self) -> str:
        """Repository name (defaults to the product key)."""
        return self.repo or self.product

    def github_repo(self) -> str:
        """``owner/repo`` slug for the product repository."""
        return f"{self.owner}/{self.resolved_repo()}"

    def resource_group(self) -> str:
        """Dedicated Azure resource-group name for this instance."""
        return f"rg-dsf-{self.product}"
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/instance/test_spec.py -v`
Expected: PASS (4 passed).

- [ ] **Step 5: Commit**

```bash
git add src/dsf/instance/__init__.py src/dsf/instance/spec.py tests/instance/__init__.py tests/instance/test_spec.py
git commit -m "feat(instance): add InstanceSpec and label-taxonomy defaults

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 2: Plan/step/manifest models + manifest IO

**Files:**
- Modify: `src/dsf/instance/spec.py` (append models + IO)
- Test: `tests/instance/test_spec.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `tests/instance/test_spec.py`:

```python
from dsf.instance.spec import (
    InstanceManifest,
    InstancePlan,
    ProvisionStep,
    instances_dir,
    manifest_path,
    read_manifest,
    write_manifest,
)


def test_provision_step_defaults():
    step = ProvisionStep(name="x", description="does x")
    assert step.command == []
    assert step.cwd == ""
    assert step.deferred is False
    assert step.executed is False
    assert step.result == ""


def test_manifest_round_trip(tmp_path):
    spec = InstanceSpec(product="demo", owner="acme")
    plan = InstancePlan(
        product="demo",
        steps=[ProvisionStep(name="write_config", description="write manifest")],
    )
    manifest = InstanceManifest(spec=spec, plan=plan, executed=False)

    path = write_manifest(manifest, repo_root=tmp_path)

    assert path == manifest_path("demo", repo_root=tmp_path)
    assert path == instances_dir(tmp_path) / "demo.json"
    assert path.exists()

    loaded = read_manifest("demo", repo_root=tmp_path)
    assert loaded.spec.product == "demo"
    assert loaded.spec.github_repo() == "acme/demo"
    assert loaded.plan.steps[0].name == "write_config"
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/instance/test_spec.py -v`
Expected: FAIL with `ImportError: cannot import name 'ProvisionStep' from 'dsf.instance.spec'`.

- [ ] **Step 3: Write minimal implementation**

Append to `src/dsf/instance/spec.py` (after `InstanceSpec`). Add `from pathlib import Path` to the imports at the top of the file (the `json` module is **not** needed — the IO uses pydantic's `model_dump_json`/`model_validate_json`):

```python
class ProvisionStep(BaseModel):
    """A single ordered provisioning action."""

    name: str
    description: str
    command: list[str] = Field(default_factory=list)
    cwd: str = ""
    deferred: bool = False
    executed: bool = False
    result: str = ""


class InstancePlan(BaseModel):
    """Ordered provisioning steps for one instance."""

    product: str
    steps: list[ProvisionStep]


class InstanceManifest(BaseModel):
    """Persisted record of an instance: spec + plan + execution status."""

    spec: InstanceSpec
    plan: InstancePlan
    executed: bool = False


def _default_repo_root() -> Path:
    """Repo root (where ``config/`` lives): three parents up from this file."""
    return Path(__file__).resolve().parents[3]


def instances_dir(repo_root: Path | None = None) -> Path:
    """Directory holding per-instance manifests (``config/instances/``)."""
    root = repo_root if repo_root is not None else _default_repo_root()
    return root / "config" / "instances"


def manifest_path(product: str, repo_root: Path | None = None) -> Path:
    """Path to a product's instance manifest."""
    return instances_dir(repo_root) / f"{product}.json"


def write_manifest(manifest: InstanceManifest, repo_root: Path | None = None) -> Path:
    """Write a manifest to ``config/instances/<product>.json`` and return the path."""
    path = manifest_path(manifest.spec.product, repo_root)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(manifest.model_dump_json(indent=2), encoding="utf-8")
    return path


def read_manifest(product: str, repo_root: Path | None = None) -> InstanceManifest:
    """Read a product's instance manifest."""
    path = manifest_path(product, repo_root)
    return InstanceManifest.model_validate_json(path.read_text(encoding="utf-8"))
```

> Remove the now-unused `import json` if `ruff` flags it (the implementation above uses `model_dump_json`/`model_validate_json`, so `json` is **not** needed — do not add it). Keep only `from pathlib import Path` as the new import.

Then update `src/dsf/instance/__init__.py` to export the new names (full file content):

```python
"""Instance provisioning — turn an InstanceSpec into a product factory instance."""

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
    "InstanceSpec",
    "ProvisionStep",
    "default_label_taxonomy",
    "instances_dir",
    "manifest_path",
    "read_manifest",
    "write_manifest",
]
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/instance/test_spec.py -v`
Expected: PASS (6 passed).

- [ ] **Step 5: Commit**

```bash
git add src/dsf/instance/spec.py tests/instance/test_spec.py
git commit -m "feat(instance): add plan/step/manifest models and manifest IO

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 3: Provisioner `plan()` (pure, ordered plan)

**Files:**
- Create: `src/dsf/instance/provisioner.py`
- Modify: `src/dsf/instance/__init__.py` (export `InstanceProvisioner`)
- Test: `tests/instance/test_provisioner.py`

- [ ] **Step 1: Write the failing test**

Create `tests/instance/test_provisioner.py`:

```python
"""Tests for InstanceProvisioner.plan() and apply()."""

from __future__ import annotations

from unittest.mock import MagicMock

from dsf.instance.provisioner import InstanceProvisioner
from dsf.instance.spec import InstanceSpec


def _spec() -> InstanceSpec:
    return InstanceSpec(product="demo", owner="acme")


def test_plan_step_order_and_names():
    plan = InstanceProvisioner(_spec()).plan()
    assert plan.product == "demo"
    assert [s.name for s in plan.steps] == [
        "create_repo",
        "squad_init",
        "squad_copilot",
        "provision_azure",
        "deploy_council",
        "deploy_sre",
        "write_config",
    ]


def test_plan_deferred_flags():
    plan = InstanceProvisioner(_spec()).plan()
    deferred = {s.name for s in plan.steps if s.deferred}
    assert deferred == {"provision_azure", "deploy_council", "deploy_sre"}


def test_plan_create_repo_command():
    plan = InstanceProvisioner(_spec()).plan()
    create = next(s for s in plan.steps if s.name == "create_repo")
    assert create.command[:3] == ["gh", "repo", "create"]
    assert "acme/demo" in create.command
    assert "--private" in create.command


def test_plan_squad_steps_run_in_repo_dir():
    plan = InstanceProvisioner(_spec()).plan()
    for name in ("squad_init", "squad_copilot"):
        step = next(s for s in plan.steps if s.name == name)
        assert step.cwd == "demo"
        assert step.command[0] == "squad"
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/instance/test_provisioner.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'dsf.instance.provisioner'`.

- [ ] **Step 3: Write minimal implementation**

Create `src/dsf/instance/provisioner.py`:

```python
"""InstanceProvisioner — build and apply a provisioning plan for an instance.

External CLIs (``gh``, ``squad``, ``az``) are invoked through an injectable
``run`` callable (defaults to :func:`subprocess.run`) so tests stay offline,
mirroring :class:`dsf.github_client.RealGitHubClient`.
"""

from __future__ import annotations

import subprocess
from collections.abc import Callable
from pathlib import Path
from typing import Any

from dsf.instance.spec import (
    InstanceManifest,
    InstancePlan,
    InstanceSpec,
    ProvisionStep,
    manifest_path,
    write_manifest,
)

Runner = Callable[..., Any]


class InstanceProvisioner:
    """Builds the ordered plan for an instance and applies it.

    Parameters
    ----------
    spec:
        Desired instance state.
    run:
        Optional ``subprocess.run``-compatible callable. Inject a mock in tests.
    repo_root:
        Optional override for where ``config/instances/`` lives (tests/CI).
    """

    def __init__(
        self,
        spec: InstanceSpec,
        *,
        run: Runner | None = None,
        repo_root: Path | None = None,
    ) -> None:
        self.spec = spec
        self._run = run or subprocess.run
        self._repo_root = repo_root

    def plan(self) -> InstancePlan:
        """Return the ordered provisioning plan (pure — no side effects)."""
        s = self.spec
        repo_dir = s.resolved_repo()
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
                name="provision_azure",
                description=(
                    f"Provision dedicated Azure resource group {s.resource_group()} "
                    "(deferred to SP2)"
                ),
                command=[
                    "az", "group", "create",
                    "--name", s.resource_group(),
                    "--location", "swedencentral",
                ],
                deferred=True,
            ),
            ProvisionStep(
                name="deploy_council",
                description=f"Deploy feature-council runtime scoped to {s.product} (deferred to SP3)",
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
        return InstancePlan(product=s.product, steps=steps)
```

Update `src/dsf/instance/__init__.py` to also export the provisioner (full file content):

```python
"""Instance provisioning — turn an InstanceSpec into a product factory instance."""

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
    "manifest_path",
    "read_manifest",
    "write_manifest",
]
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/instance/test_provisioner.py -v`
Expected: PASS (4 passed).

- [ ] **Step 5: Commit**

```bash
git add src/dsf/instance/provisioner.py src/dsf/instance/__init__.py tests/instance/test_provisioner.py
git commit -m "feat(instance): build ordered provisioning plan

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 4: Provisioner `apply()` — dry-run, execute, idempotency

**Files:**
- Modify: `src/dsf/instance/provisioner.py` (add `apply`, `_repo_exists`)
- Test: `tests/instance/test_provisioner.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `tests/instance/test_provisioner.py`:

```python
def test_apply_dry_run_writes_manifest_and_runs_nothing(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return MagicMock(returncode=0)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=False)

    assert calls == []  # dry-run shells out to nothing
    assert manifest.executed is False
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["create_repo"] == "dry-run"
    assert results["provision_azure"] == "deferred"
    assert results["write_config"].endswith("demo.json")
    assert (tmp_path / "config" / "instances" / "demo.json").exists()


def test_apply_execute_runs_real_steps_and_stubs_deferred(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append((cmd, kwargs.get("cwd")))
        returncode = 1 if cmd[:3] == ["gh", "repo", "view"] else 0
        return MagicMock(returncode=returncode)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    executed = [cmd for cmd, _ in calls]
    # repo created and squad initialized in the cloned repo dir:
    assert ["gh", "repo", "create", "acme/demo", "--private", "--clone"] in executed
    squad_init = next((cmd, cwd) for cmd, cwd in calls if cmd[:2] == ["squad", "init"])
    assert squad_init[1] == "demo"
    # deferred subsystems are never invoked in SP1:
    assert not any(cmd[0] == "az" for cmd, _ in calls)
    assert manifest.executed is True
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["create_repo"] == "executed"
    assert results["deploy_council"] == "deferred"


def test_apply_execute_is_idempotent_when_repo_exists(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        returncode = 0 if cmd[:3] == ["gh", "repo", "view"] else 0
        return MagicMock(returncode=returncode)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    # repo already exists -> create is skipped:
    assert ["gh", "repo", "create", "acme/demo", "--private", "--clone"] not in calls
    create = next(s for s in manifest.plan.steps if s.name == "create_repo")
    assert create.result == "exists"
    assert create.executed is True
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/instance/test_provisioner.py -v`
Expected: FAIL with `AttributeError: 'InstanceProvisioner' object has no attribute 'apply'`.

- [ ] **Step 3: Write minimal implementation**

Append these two methods to `InstanceProvisioner` in `src/dsf/instance/provisioner.py`:

```python
    def apply(self, *, execute: bool = False) -> InstanceManifest:
        """Apply the plan. ``execute=False`` is a side-effect-free dry-run that
        still writes the manifest; ``execute=True`` runs the non-deferred,
        commanded steps via the injected runner.
        """
        plan = self.plan()
        for step in plan.steps:
            if step.name == "write_config":
                continue  # finalized after the manifest is built
            if step.deferred:
                step.result = "deferred"
            elif not execute:
                step.result = "dry-run"
            elif not step.command:
                step.result = "noop"
            elif step.name == "create_repo" and self._repo_exists():
                step.executed, step.result = True, "exists"
            else:
                kwargs = {"cwd": step.cwd} if step.cwd else {}
                self._run(step.command, check=True, **kwargs)
                step.executed, step.result = True, "executed"

        manifest = InstanceManifest(spec=self.spec, plan=plan, executed=execute)
        path = manifest_path(self.spec.product, self._repo_root)
        for step in plan.steps:
            if step.name == "write_config":
                step.executed, step.result = True, str(path)
        write_manifest(manifest, self._repo_root)
        return manifest

    def _repo_exists(self) -> bool:
        """Return True if the product repo already exists (``gh repo view``)."""
        result = self._run(
            ["gh", "repo", "view", self.spec.github_repo()],
            capture_output=True,
            text=True,
            check=False,
        )
        return getattr(result, "returncode", 1) == 0
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/instance/test_provisioner.py -v`
Expected: PASS (7 passed).

- [ ] **Step 5: Commit**

```bash
git add src/dsf/instance/provisioner.py tests/instance/test_provisioner.py
git commit -m "feat(instance): apply plan with dry-run, execute, and idempotency

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 5: CLI `new` subcommand

**Files:**
- Modify: `src/dsf/cli.py` (add `_cmd_new`, `_print_plan`, parser wiring)
- Test: `tests/instance/test_cli_new.py`

- [ ] **Step 1: Write the failing test**

Create `tests/instance/test_cli_new.py`:

```python
"""Tests for the `dsf new` CLI subcommand."""

from __future__ import annotations

from dsf.cli import build_parser, main


def test_new_parser_wiring():
    args = build_parser().parse_args(["new", "--product", "demo", "--owner", "acme"])
    assert args.command == "new"
    assert args.product == "demo"
    assert args.owner == "acme"
    assert args.execute is False
    assert args.write_plan is False


def test_new_dry_run_prints_plan_without_side_effects(capsys, tmp_path):
    rc = main(["new", "--product", "demo", "--owner", "acme"])
    out = capsys.readouterr().out
    assert rc == 0
    assert "create_repo" in out
    assert "squad_init" in out
    assert "deferred" in out
    # pure preview: no manifest written anywhere under tmp_path
    assert not (tmp_path / "config" / "instances" / "demo.json").exists()


def test_new_write_plan_writes_manifest(tmp_path):
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--write-plan", "--config-root", str(tmp_path),
    ])
    assert rc == 0
    assert (tmp_path / "config" / "instances" / "demo.json").exists()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/instance/test_cli_new.py -v`
Expected: FAIL — `argparse` exits with `invalid choice: 'new'` (SystemExit) on the first test.

- [ ] **Step 3: Write minimal implementation**

In `src/dsf/cli.py`, add the command handler and printer (place them above `build_parser`, after `_cmd_control_center`):

```python
def _print_plan(plan, *, execute: bool = False) -> None:
    """Print an instance provisioning plan in a compact, readable form."""
    mode = "EXECUTE" if execute else "DRY-RUN"
    print(f"[dsf] instance plan for product={plan.product} ({mode})")
    for i, step in enumerate(plan.steps, 1):
        status = step.result or ("deferred" if step.deferred else "planned")
        print(f"[dsf]  {i}. {step.name:14s} [{status}] {step.description}")
        if step.command:
            print(f"[dsf]       $ {' '.join(step.command)}")


def _cmd_new(args: argparse.Namespace) -> int:
    """Create (or preview) a new isolated product factory instance."""
    from dsf.instance.provisioner import InstanceProvisioner
    from dsf.instance.spec import InstanceSpec

    spec = InstanceSpec(
        product=args.product,
        owner=args.owner,
        repo=args.repo or "",
        visibility=args.visibility,
        runtime_target=args.runtime_target,
    )
    root = Path(args.config_root) if args.config_root else None
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

Then register the subcommand inside `build_parser()`, immediately before `return parser`:

```python
    p_new = sub.add_parser("new", help="create a new isolated product factory instance")
    p_new.add_argument("--product", required=True, help="product key (e.g. 'microbi')")
    p_new.add_argument("--owner", required=True, help="GitHub owner/org for the product repo")
    p_new.add_argument("--repo", default="", help="repo name (defaults to product key)")
    p_new.add_argument(
        "--visibility", default="private",
        choices=["private", "public", "internal"], help="product repo visibility",
    )
    p_new.add_argument(
        "--runtime-target", default="homelab",
        choices=["homelab", "aca"], help="where the factory runtime is hosted",
    )
    p_new.add_argument(
        "--execute", action="store_true",
        help="run executable steps (gh/squad); Azure/council/SRE remain deferred",
    )
    p_new.add_argument(
        "--write-plan", action="store_true",
        help="dry-run, but write the instance manifest to config/instances/",
    )
    p_new.add_argument(
        "--config-root", default=None,
        help="override repo root where config/instances/ is written (tests/CI)",
    )
    p_new.set_defaults(func=_cmd_new)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/instance/test_cli_new.py -v`
Expected: PASS (3 passed).

- [ ] **Step 5: Commit**

```bash
git add src/dsf/cli.py tests/instance/test_cli_new.py
git commit -m "feat(cli): add 'dsf new' to scaffold a product instance

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 6: Docs, Make target, and full verification

**Files:**
- Modify: `Makefile`
- Modify: `README.md`
- Modify: `docs/RUNBOOK.md`

- [ ] **Step 1: Add a convenience Make target**

In `Makefile`, add `new-demo` to the `.PHONY` line and append this target:

```makefile
new-demo:
	uv run python -m dsf.cli new --product demo --owner your-org
```

- [ ] **Step 2: Document in README layout**

In `README.md`, under the `## Layout` section, append this sentence to the `src/dsf/` description:

```
`instance/` — instance spec + provisioner powering the `dsf new` CLI (greenfield product-factory scaffolding; Azure/council/SRE steps deferred to later sub-projects).
```

- [ ] **Step 3: Document in RUNBOOK**

In `docs/RUNBOOK.md`, add this subsection after "## Running pieces individually":

```markdown
## Creating a product instance (SP1)

`dsf new` scaffolds an isolated product factory. Greenfield walking skeleton:
repo creation + Coding Squad init are real (under `--execute`); Azure, feature
council, and SRE deployment are **deferred** stub steps (SP2/SP3/SP5).

```bash
# Preview the plan (no side effects):
uv run python -m dsf.cli new --product microbi --owner your-org

# Preview AND write the instance manifest to config/instances/microbi.json:
uv run python -m dsf.cli new --product microbi --owner your-org --write-plan

# Execute: create the GitHub repo + initialize Squad (needs gh + @bradygaster/squad-cli):
uv run python -m dsf.cli new --product microbi --owner your-org --execute
```
```

- [ ] **Step 4: Verify the whole suite, lint, and dry-run smoke**

Run: `uv run ruff check . && uv run pytest -q && uv run python -m dsf.cli new --product demo --owner your-org`
Expected: ruff clean; all tests pass (prior count + 16 new); CLI prints a 7-step DRY-RUN plan ending with `write_config`.

- [ ] **Step 5: Commit**

```bash
git add Makefile README.md docs/RUNBOOK.md
git commit -m "docs(instance): document 'dsf new' and add make target

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Done criteria

- `uv run pytest -q` passes (16 new tests across `tests/instance/`).
- `uv run ruff check .` is clean.
- `python -m dsf.cli new --product demo --owner your-org` prints a 7-step plan; `--write-plan` writes `config/instances/demo.json`; `--execute` shells out only to `gh`/`squad` (Azure/council/SRE remain `deferred`).
- No real network/CLI calls in tests (all behind the injected runner).

## Out of scope (later sub-projects)

- Real Azure provisioning (SP2), `build_services('azure')` + council deploy (SP3), Squad handoff hardening (SP4), SRE agent (SP5), brownfield `dsf onboard` (SP6), lifecycle `status`/`upgrade`/`destroy` (SP7).
