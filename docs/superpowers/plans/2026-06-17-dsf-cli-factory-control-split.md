# DSF CLI Carve-out (`dsf` factory + `dsfctl` control) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the single `src/dsf/cli.py` into a `src/dsf/cli/` package with two console-script entry points — `dsf` (factory: create product instances) and `dsfctl` (instance control: operate the feature-council runtime) — and migrate every caller, with no `python -m dsf.cli` shim.

**Architecture:** One `dsf` Python package (ADR 0001) exposing two argparse CLIs via `[project.scripts]`. `dsf.cli.factory:main` → `dsf` (owns `new`). `dsf.cli.control:main` → `dsfctl` (owns `run`/`sweep`/`serve-orchestrator`/`serve-agent`/`control-center` + the global `--mode` flag). Behaviour-preserving move; the only command-surface change is which entry point each subcommand lives under.

**Tech Stack:** Python 3.12, argparse, hatchling console scripts, pytest, ruff, uv. Spec: `docs/superpowers/specs/2026-06-17-dsf-cli-factory-control-split-design.md`.

---

## File structure

**Created:**
- `src/dsf/cli/__init__.py` — package docstring; no re-exports.
- `src/dsf/cli/control.py` — the `dsfctl` CLI (runtime ops + `--mode`).
- `src/dsf/cli/factory.py` — the `dsf` CLI (`new`, future lifecycle).
- `tests/cli/__init__.py` — test package marker.
- `tests/cli/test_control.py` — moved runtime CLI tests + separation test.
- `tests/cli/test_factory.py` — moved `new` CLI tests + separation test.
- `docs/adr/0003-two-clis-factory-and-instance-control.md` — the decision record.

**Modified:**
- `pyproject.toml` — add `[project.scripts]`.
- `src/dsf/runtime/Dockerfile` — CMD → `dsfctl`.
- `tests/runtime/test_runtime_image.py` — assert the new CMD.
- `tests/test_container.py` — drop the moved CLI tests + now-unused imports.
- `tests/e2e/test_dry_run_line.py` — import from `dsf.cli.control`.
- `Makefile` — `dryrun`/`new-demo` targets.
- `README.md`, `docs/RUNBOOK.md` — new commands + fix broken `dsf new` example.
- `docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md` — mark the roadmap row done.

**Deleted:**
- `src/dsf/cli.py`.
- `tests/instance/test_cli_new.py` (moved to `tests/cli/test_factory.py`).

---

## Task 1: Cut `dsf.cli` over to the `dsf`/`dsfctl` two-CLI package

This is the atomic structural move: a module→package conversion cannot keep both
`src/dsf/cli.py` and `src/dsf/cli/` at once, so the package creation, all Python
import rewiring, the Dockerfile CMD, and the console scripts land together and the
full suite is verified green at the end.

**Files:**
- Create: `src/dsf/cli/__init__.py`, `src/dsf/cli/control.py`, `src/dsf/cli/factory.py`
- Create: `tests/cli/__init__.py`, `tests/cli/test_control.py`, `tests/cli/test_factory.py`
- Delete: `src/dsf/cli.py`, `tests/instance/test_cli_new.py`
- Modify: `pyproject.toml`, `src/dsf/runtime/Dockerfile`, `tests/runtime/test_runtime_image.py:24-30`, `tests/test_container.py:1-9,115-169`, `tests/e2e/test_dry_run_line.py:11`

- [ ] **Step 1: Write the new test files first (they import the not-yet-existing modules)**

Create `tests/cli/__init__.py` (empty):

```python
```

Create `tests/cli/test_factory.py`:

```python
"""Tests for the `dsf` factory CLI (`dsf new`)."""

from __future__ import annotations

import pytest

from dsf.cli.factory import build_parser, main
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


def test_factory_parser_rejects_runtime_command():
    # the factory CLI must NOT expose runtime ops — those live in dsfctl
    with pytest.raises(SystemExit):
        build_parser().parse_args(["run"])
```

Create `tests/cli/test_control.py`:

```python
"""Tests for the `dsfctl` instance-control CLI (feature-council runtime ops)."""

from __future__ import annotations

import json

import pytest

from dsf.cli.control import build_parser, main


def test_cli_azure_mode_without_product_exits_cleanly(capsys, monkeypatch):
    monkeypatch.delenv("DSF_PRODUCT", raising=False)
    with pytest.raises(SystemExit) as exc_info:
        main(["--mode", "azure", "sweep"])
    assert exc_info.value.code == 1
    assert "DSF_PRODUCT" in capsys.readouterr().err


def test_cli_run_dry_run_with_signal(tmp_path, capsys):
    signal = tmp_path / "signal.json"
    signal.write_text(json.dumps({"alert": "boom", "level": "error"}), encoding="utf-8")
    rc = main(["run", "--dry-run", "--signal", str(signal)])
    assert rc == 0
    out = capsys.readouterr().out
    assert "run" in out
    assert "dry_run=True" in out


def test_cli_run_missing_signal_returns_error(capsys):
    rc = main(["run", "--signal", "does-not-exist.json"])
    assert rc == 1


def test_cli_subcommands_importable():
    parser = build_parser()
    for cmd in ("run", "sweep", "serve-agent", "serve-orchestrator", "control-center"):
        args = parser.parse_args([cmd])
        assert args.command == cmd


def test_cli_sweep_runs_line(capsys):
    assert main(["sweep"]) == 0
    assert "status=" in capsys.readouterr().out


def test_cli_serve_agent_unknown_kind_errors():
    assert main(["serve-agent", "--kind", "nope"]) == 1


def test_cli_serve_commands_launch_uvicorn(monkeypatch):
    launched: list[str] = []
    monkeypatch.setattr("uvicorn.run", lambda target, **kw: launched.append(target))
    assert main(["serve-agent", "--kind", "sentry"]) == 0
    assert main(["control-center"]) == 0
    assert launched == ["dsf.agents.sentry.main:app", "dsf.control_center.app:app"]


def test_cli_unsupported_mode_exits_cleanly(capsys):
    """An unsupported --mode must exit non-zero with a clear message, no traceback."""
    with pytest.raises(SystemExit) as exc_info:
        main(["--mode", "gcp", "sweep"])
    assert exc_info.value.code == 1
    err = capsys.readouterr().err
    assert "not yet supported" in err
    assert "gcp" in err


def test_control_parser_rejects_new_command():
    # the control CLI must NOT expose `new` — that lives in the dsf factory CLI
    with pytest.raises(SystemExit):
        build_parser().parse_args(["new"])
```

- [ ] **Step 2: Run the new tests to verify they fail (modules don't exist)**

Run: `uv run pytest tests/cli/ -q`
Expected: FAIL — `ModuleNotFoundError: No module named 'dsf.cli.control'` (collection error).

- [ ] **Step 3: Delete the old module so the package name is free**

```bash
git rm src/dsf/cli.py
```

- [ ] **Step 4: Create the package marker `src/dsf/cli/__init__.py`**

```python
"""DSF command-line entry points.

Two console scripts live here, both inside the single ``dsf`` package (ADR 0001):

* ``dsf``    (:mod:`dsf.cli.factory`) — create/manage product instances from the
  template (``dsf new``; future ``status``/``upgrade``/``destroy``).
* ``dsfctl`` (:mod:`dsf.cli.control`) — operate a running instance's feature-council
  runtime (``run``/``sweep``/``serve-orchestrator``/``serve-agent``/``control-center``).
"""
```

- [ ] **Step 5: Create `src/dsf/cli/control.py` (the `dsfctl` CLI)**

```python
"""``dsfctl`` — operate a running instance's feature-council runtime.

``run``/``sweep`` execute the conveyor in-process (local fakes by default, fully
dry-run safe). ``serve-agent``/``serve-orchestrator``/``control-center`` launch the
respective ASGI services via uvicorn. The global ``--mode`` flag selects the service
bundle (``local``/``gh``/``azure``).
"""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
from pathlib import Path

from dsf.container import build_services


def _print_run_summary(run) -> None:
    """Print a compact summary of a finished run."""
    print(f"[dsf] run {run.id} -> status={run.status.value} (dry_run={run.dry_run})")
    print(f"[dsf]   evidence={len(run.evidence)} proposals={len(run.proposals)}")
    for rec in run.audit:
        print(f"[dsf]   audit[{rec.station}] {rec.message}")


def _get_services(mode: str):
    """Build a services bundle or exit cleanly on unsupported/misconfigured modes."""
    try:
        return build_services(mode)
    except (NotImplementedError, ValueError) as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        sys.exit(1)


def _cmd_run(args: argparse.Namespace) -> int:
    """Run the intake line for one signal JSON file."""
    from dsf.orchestrator.conveyor import run_line
    from dsf.triggers.ingestion import signal_to_run

    services = _get_services(args.mode)
    if not args.signal:
        print("--signal <path> is required for `run`", file=sys.stderr)
        return 1
    path = Path(args.signal)
    if not path.exists():
        print(f"signal file not found: {path}", file=sys.stderr)
        return 1
    payload = json.loads(path.read_text(encoding="utf-8"))
    run = signal_to_run(payload)
    if args.dry_run or services.config.is_enabled("dry_run"):
        run.dry_run = True
    final = asyncio.run(run_line(run, services))
    _print_run_summary(final)
    return 0


def _cmd_sweep(args: argparse.Namespace) -> int:
    """Run a scheduled sweep across enabled sources."""
    from dsf.triggers.scheduler import run_sweep

    services = _get_services(args.mode)
    final = asyncio.run(run_sweep(services))
    _print_run_summary(final)
    return 0


def _cmd_serve_orchestrator(args: argparse.Namespace) -> int:
    """One-shot orchestrator worker (a real deployment would loop on a queue)."""
    from dsf.triggers.scheduler import run_sweep

    services = _get_services(args.mode)
    final = asyncio.run(run_sweep(services))
    _print_run_summary(final)
    return 0


_AGENT_MODULES = {
    "sentry": "dsf.agents.sentry.main:app",
    "grafana": "dsf.agents.grafana.main:app",
    "foundryiq": "dsf.agents.foundryiq.main:app",
    "webiq": "dsf.agents.webiq.main:app",
    "tickets": "dsf.agents.tickets.main:app",
}


def _cmd_serve_agent(args: argparse.Namespace) -> int:
    """Serve a source agent over A2A via uvicorn."""
    import uvicorn

    target = _AGENT_MODULES.get(args.kind)
    if target is None:
        choices = sorted(_AGENT_MODULES)
        print(f"unknown agent kind: {args.kind} (choices: {choices})", file=sys.stderr)
        return 1
    uvicorn.run(target, host=args.host, port=args.port)
    return 0


def _cmd_control_center(args: argparse.Namespace) -> int:
    """Serve the Control Center web UI via uvicorn."""
    import uvicorn

    uvicorn.run("dsf.control_center.app:app", host=args.host, port=args.port)
    return 0


def build_parser() -> argparse.ArgumentParser:
    """Build the ``dsfctl`` parser with all runtime subcommands."""
    parser = argparse.ArgumentParser(
        prog="dsfctl",
        description="Dark Software Factory — instance control CLI (feature-council runtime)",
    )
    parser.add_argument(
        "--mode",
        default="local",
        help=(
            "service mode: 'local' (in-memory fakes, default), 'gh' (real GitHub "
            "client via gh CLI), or 'azure' (per-product runtime; requires "
            "DSF_PRODUCT). Other modes are not yet supported."
        ),
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p_run = sub.add_parser("run", help="run the intake line for one signal")
    p_run.add_argument("--dry-run", action="store_true", help="run line, skip filing")
    p_run.add_argument("--signal", help="path to a signal JSON file")
    p_run.set_defaults(func=_cmd_run)

    p_sweep = sub.add_parser("sweep", help="run a scheduled sweep")
    p_sweep.set_defaults(func=_cmd_sweep)

    p_orch = sub.add_parser(
        "serve-orchestrator", help="run the orchestrator worker (one-shot sweep)"
    )
    p_orch.set_defaults(func=_cmd_serve_orchestrator)

    p_serve = sub.add_parser("serve-agent", help="serve a source agent over A2A")
    p_serve.add_argument("--kind", default="sentry", help="source agent kind")
    p_serve.add_argument("--host", default="0.0.0.0", help="bind host")
    p_serve.add_argument("--port", type=int, default=8080, help="bind port")
    p_serve.set_defaults(func=_cmd_serve_agent)

    p_cc = sub.add_parser("control-center", help="serve the control center UI")
    p_cc.add_argument("--host", default="127.0.0.1", help="bind host (localhost-only by default)")
    p_cc.add_argument("--port", type=int, default=8081, help="bind port")
    p_cc.set_defaults(func=_cmd_control_center)

    return parser


def main(argv: list[str] | None = None) -> int:
    """Parse args and dispatch. Returns a process exit code."""
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
```

- [ ] **Step 6: Create `src/dsf/cli/factory.py` (the `dsf` CLI)**

```python
"""``dsf`` — create and manage product factory instances from the template.

``dsf new`` provisions an isolated product factory: its own GitHub repo + Coding
Squad, a dedicated Azure resource group, and the per-product feature-council runtime
rendered as a homelab compose bundle. Future lifecycle verbs (``status``/``upgrade``/
``destroy``, SP7) will live here too.
"""

from __future__ import annotations

import argparse
from pathlib import Path


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


def build_parser() -> argparse.ArgumentParser:
    """Build the ``dsf`` parser with the instance-lifecycle subcommands."""
    parser = argparse.ArgumentParser(
        prog="dsf",
        description="Dark Software Factory — factory CLI (create product instances)",
    )
    sub = parser.add_subparsers(dest="command", required=True)

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
    p_new.add_argument(
        "--execute", action="store_true",
        help="run executable steps (gh/squad/az + council bring-up); SRE deploy stays deferred",
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

    return parser


def main(argv: list[str] | None = None) -> int:
    """Parse args and dispatch. Returns a process exit code."""
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
```

- [ ] **Step 7: Delete the old `new` test file (moved to `tests/cli/test_factory.py`)**

```bash
git rm tests/instance/test_cli_new.py
```

- [ ] **Step 8: Remove the moved CLI tests + now-unused imports from `tests/test_container.py`**

Delete lines 115-169 (the eight `test_cli_*` functions, from
`def test_cli_azure_mode_without_product_exits_cleanly` through the end of
`test_cli_unsupported_mode_exits_cleanly`). The file should end at line 113 (the
`test_build_services_azure_missing_product_raises_value_error` function).

Then change the imports at the top: replace lines 5 and 9.

Replace:

```python
import json

import pytest

from dsf.cli import build_parser, main
from dsf.container import AzureRuntimeSettings, Services, build_services
```

with (drop `import json` and the `dsf.cli` import — neither is used by the remaining
container tests; keep `pytest`):

```python
import pytest

from dsf.container import AzureRuntimeSettings, Services, build_services
```

- [ ] **Step 9: Point the e2e test's CLI import at `dsf.cli.control`**

In `tests/e2e/test_dry_run_line.py:11` replace:

```python
from dsf.cli import main
```

with:

```python
from dsf.cli.control import main
```

- [ ] **Step 10: Update the Dockerfile CMD to the `dsfctl` console script**

In `src/dsf/runtime/Dockerfile`, replace the trailing comment + CMD:

```dockerfile
# Per-product feature-council orchestrator. Scope is supplied at runtime via
# DSF_PRODUCT (see .env.orchestrator). The global --mode flag MUST precede the
# subcommand.
CMD ["python", "-m", "dsf.cli", "--mode", "azure", "serve-orchestrator"]
```

with:

```dockerfile
# Per-product feature-council orchestrator (the dsfctl instance-control CLI).
# Scope is supplied at runtime via DSF_PRODUCT (see .env.orchestrator). The
# global --mode flag MUST precede the subcommand.
CMD ["dsfctl", "--mode", "azure", "serve-orchestrator"]
```

- [ ] **Step 11: Update the Dockerfile CMD assertion**

In `tests/runtime/test_runtime_image.py:27-30` replace:

```python
    assert (
        'CMD ["python", "-m", "dsf.cli", "--mode", "azure", "serve-orchestrator"]'
        in text
    )
```

with:

```python
    assert 'CMD ["dsfctl", "--mode", "azure", "serve-orchestrator"]' in text
```

- [ ] **Step 12: Add the console scripts to `pyproject.toml`**

Insert a `[project.scripts]` table immediately after the `dependencies = [...]`
block closes (after line 14, before `[project.optional-dependencies]`):

```toml
[project.scripts]
dsf = "dsf.cli.factory:main"
dsfctl = "dsf.cli.control:main"
```

- [ ] **Step 13: Re-install so the console scripts register, then run the new tests**

Run: `uv pip install -e ".[dev]" -q && uv run pytest tests/cli/ -q`
Expected: PASS — all `tests/cli/` tests green (12 tests).

- [ ] **Step 14: Run the full suite + lint to confirm nothing else broke**

Run: `uv run pytest -q && uv run ruff check .`
Expected: PASS — full suite green (same count as before, ~300), `ruff` reports "All checks passed!".

- [ ] **Step 15: Smoke-test both console entry points resolve**

Run: `uv run dsf new --product demo --owner your-org --name-prefix demo --config-root /tmp/dsf-smoke && uv run dsfctl run --dry-run --signal tests/fixtures/sample_signal.json`
Expected: the first prints `instance plan for product=demo (DRY-RUN)`; the second prints a run summary ending `status=filed`.

- [ ] **Step 16: Commit**

```bash
git add src/dsf/cli/ tests/cli/ pyproject.toml src/dsf/runtime/Dockerfile \
  tests/runtime/test_runtime_image.py tests/test_container.py tests/e2e/test_dry_run_line.py
git -c commit.gpgsign=false commit -m "refactor(cli): split dsf.cli into dsf (factory) + dsfctl (control)

Convert src/dsf/cli.py into a src/dsf/cli/ package with two console-script
entry points in the single dsf package (ADR 0001): 'dsf' (dsf.cli.factory)
owns 'new'; 'dsfctl' (dsf.cli.control) owns run/sweep/serve-*/control-center
and the global --mode flag. Migrate all Python callers + the runtime Dockerfile
CMD; no python -m dsf.cli shim. Tests moved to tests/cli/ with disjointness checks.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 2: Migrate the Makefile targets

**Files:**
- Modify: `Makefile` (`dryrun`, `new-demo` targets)

- [ ] **Step 1: Update the `dryrun` and `new-demo` recipes**

In `Makefile`, replace:

```makefile
dryrun:
	uv run python -m dsf.cli run --dry-run --signal tests/fixtures/sample_signal.json
```

with:

```makefile
dryrun:
	uv run dsfctl run --dry-run --signal tests/fixtures/sample_signal.json
```

and replace:

```makefile
new-demo:
	uv run python -m dsf.cli new --product demo --owner your-org --name-prefix demo
```

with:

```makefile
new-demo:
	uv run dsf new --product demo --owner your-org --name-prefix demo
```

- [ ] **Step 2: Verify both targets run**

Run: `make dryrun && make new-demo`
Expected: `make dryrun` prints a run summary ending `status=filed`; `make new-demo`
prints `instance plan for product=demo (DRY-RUN)`.

- [ ] **Step 3: Commit**

```bash
git add Makefile
git -c commit.gpgsign=false commit -m "chore(make): point dryrun/new-demo at dsfctl/dsf console scripts

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 3: Record the decision in ADR 0003

**Files:**
- Create: `docs/adr/0003-two-clis-factory-and-instance-control.md`

- [ ] **Step 1: Write the ADR**

Create `docs/adr/0003-two-clis-factory-and-instance-control.md`:

```markdown
# ADR 0003 — Two CLIs: `dsf` (factory) and `dsfctl` (instance control)

Status: Accepted · Date: 2026-06-17 · Supersedes the SP1 note "no console-script
entry point — keep it that way" (docs/superpowers/plans/2026-06-17-dsf-new-greenfield-instance.md)

## Context

`src/dsf/cli.py` had grown to conflate two unrelated concerns behind one
`python -m dsf.cli` entry point: operating a running instance's feature-council
runtime (`run`/`sweep`/`serve-orchestrator`/`serve-agent`/`control-center`) and
provisioning a brand-new product factory from the template (`new`). "The CLI you run
to create an instance" was tangled with "the source of the feature council you
operate," which was confusing.

## Decision

1. **Two console scripts, one package.** Per ADR 0001 the project stays a single
   `dsf` Python package. `src/dsf/cli.py` becomes a `src/dsf/cli/` package exposing
   two entry points via `[project.scripts]`:
   - **`dsf`** → `dsf.cli.factory:main` — the *factory* CLI: create/manage product
     instances from the template (`dsf new`; future SP7 `status`/`upgrade`/`destroy`).
   - **`dsfctl`** → `dsf.cli.control:main` — the *instance control* CLI (kubectl-style):
     operate a running instance's feature-council runtime, including the global
     `--mode` flag (`local`/`gh`/`azure`).

2. **Full migration, no shim.** `python -m dsf.cli` is removed; the Dockerfile,
   Makefile, tests, and living docs invoke `dsf`/`dsfctl`. Removing the old entry
   point prevents the tangled surface from lingering.

3. **Behaviour-preserving.** Each subcommand keeps its exact flags and effects; only
   the entry point it lives under changes.

## Consequences

- The runtime image runs `CMD ["dsfctl", "--mode", "azure", "serve-orchestrator"]`.
- Each module keeps a `__main__` guard, so `python -m dsf.cli.control` /
  `python -m dsf.cli.factory` also work without the console scripts being on `PATH`.
- New instance-lifecycle verbs have an obvious home (`dsf`), and runtime ops have an
  obvious home (`dsfctl`), so neither CLI accretes unrelated commands.
- This is the cross-cutting "CLI / runtime split" from the template charter roadmap.
```

- [ ] **Step 2: Commit**

```bash
git add docs/adr/0003-two-clis-factory-and-instance-control.md
git -c commit.gpgsign=false commit -m "docs(adr): add ADR 0003 — dsf (factory) + dsfctl (control) CLIs

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 4: Clean up the living docs

**Files:**
- Modify: `README.md`, `docs/RUNBOOK.md`
- Modify: `docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md`

- [ ] **Step 1: Update `README.md` — quickstart commands + fix the broken `new` example + name both CLIs**

In `README.md`, replace the Control Center quickstart line:

```bash
uv run python -m dsf.cli control-center   # http://localhost:8081
```

with:

```bash
uv run dsfctl control-center   # http://localhost:8081
```

Replace the (currently broken — missing required `--product`/`--owner`) factory
example:

```bash
uv run python -m dsf.cli new microbi --name-prefix microbi   # preview the 8-step plan
```

with:

```bash
uv run dsf new --product microbi --owner your-org --name-prefix microbi   # preview the 8-step plan
```

Then, immediately after that fenced code block, add a one-line orientation note:

```markdown
Two CLIs ship from the single `dsf` package: **`dsf`** creates/manages product
instances from the template (`dsf new`), and **`dsfctl`** operates a running
instance's feature-council runtime (`dsfctl run|sweep|serve-orchestrator|serve-agent|
control-center`).
```

- [ ] **Step 2: Update `docs/RUNBOOK.md` — every CLI invocation**

In `docs/RUNBOOK.md`, rewrite each invocation. Replace the runtime commands:

```bash
uv run python -m dsf.cli run --dry-run --signal tests/fixtures/sample_signal.json
```
→
```bash
uv run dsfctl run --dry-run --signal tests/fixtures/sample_signal.json
```

```bash
uv run python -m dsf.cli sweep
```
→
```bash
uv run dsfctl sweep
```

```bash
uv run python -m dsf.cli serve-agent --kind sentry --port 8080
```
→
```bash
uv run dsfctl serve-agent --kind sentry --port 8080
```

```bash
uv run python -m dsf.cli control-center --port 8081
```
→
```bash
uv run dsfctl control-center --port 8081
```

```bash
DSF_PRODUCT=microbi uv run python -m dsf.cli --mode azure serve-orchestrator
```
→
```bash
DSF_PRODUCT=microbi uv run dsfctl --mode azure serve-orchestrator
```

And the three factory invocations in the "Creating a product instance" section:

```bash
uv run python -m dsf.cli new --product microbi --owner your-org --name-prefix microbi
```
→
```bash
uv run dsf new --product microbi --owner your-org --name-prefix microbi
```

```bash
uv run python -m dsf.cli new --product microbi --owner your-org --name-prefix microbi --write-plan
```
→
```bash
uv run dsf new --product microbi --owner your-org --name-prefix microbi --write-plan
```

```bash
uv run python -m dsf.cli new --product microbi --owner your-org --name-prefix microbi --execute
```
→
```bash
uv run dsf new --product microbi --owner your-org --name-prefix microbi --execute
```

- [ ] **Step 3: Mark the charter roadmap row done**

In `docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md`,
replace the cross-cutting roadmap row:

```markdown
| — | CLI / runtime split *(cross-cutting)* | Separate the `dsf new` instance-provisioning CLI from the feature-council runtime source so the two concerns are not conflated. |
```

with:

```markdown
| — ✅ | CLI / runtime split *(cross-cutting, done)* | `dsf` (factory CLI, `dsf.cli.factory`) creates instances; `dsfctl` (instance control, `dsf.cli.control`) operates the feature-council runtime. Two console scripts in one package (ADR 0003). |
```

- [ ] **Step 4: Verify the docs no longer reference the old entry point**

Run: `grep -rn "python -m dsf.cli\b" README.md docs/RUNBOOK.md`
Expected: no matches (exit code 1, no output).

- [ ] **Step 5: Commit**

```bash
git add README.md docs/RUNBOOK.md docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md
git -c commit.gpgsign=false commit -m "docs: migrate README/RUNBOOK/charter to dsf + dsfctl commands

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 5: Final sweep + full verification gate

**Files:** none (verification only; commit any stragglers found).

- [ ] **Step 1: Repo-wide grep for stale `dsf.cli` references in code/build files**

Scan only executable/build files (not docs, which legitimately discuss the change),
and filter out the two new submodules so only a *bare* reference to the deleted
`dsf.cli` module is flagged:

Run: `grep -rn "dsf\.cli" . --include='*.py' --include='*.toml' --include=Dockerfile --include=Makefile | grep -v "__pycache__" | grep -v "dsf\.cli\.factory" | grep -v "dsf\.cli\.control"`
Expected: no matches. Any line that survives the filters references the deleted
module (e.g. a missed `from dsf.cli import ...` or `python -m dsf.cli` invocation) —
fix it and amend the relevant commit. (ADR 0003, the spec, and this plan are `.md`
files and are intentionally not scanned.)

- [ ] **Step 2: Run the full verification gate**

Run: `uv run ruff check . && uv run pytest -q && uv run python -m dsf.evals.runner --gate`
Expected: `ruff` "All checks passed!"; full suite green (~300 passed); evals
`GATE PASSED`.

- [ ] **Step 3: Final entry-point smoke (help text proves both consoles resolve)**

Run: `uv run dsf --help && uv run dsfctl --help`
Expected: `dsf` usage shows only the `new` subcommand; `dsfctl` usage shows
`--mode` and the `run`/`sweep`/`serve-orchestrator`/`serve-agent`/`control-center`
subcommands. The two surfaces are disjoint.

---

## Self-review notes (for the implementer)

- The split is behaviour-preserving: `control.py` and `factory.py` are verbatim moves
  of the existing handlers, only re-homed and given `prog=`/`description=` and a
  `__main__` guard each. The single intentional copy-edit is the `dsf new --execute`
  help text ("council bring-up" added; only SRE deferred), reflecting the SP3 change.
- `tests/test_container.py` keeps only container tests; `import json` and the
  `dsf.cli` import are removed because nothing remaining uses them — run ruff (Task 1
  Step 14) to confirm no unused-import error.
- No `python -m dsf.cli` shim is added anywhere (ADR 0003 decision #2). The Dockerfile
  uses the `dsfctl` console script, which the image's `pip install` places on `PATH`.
