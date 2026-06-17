# Feature-Council Runtime Productionization (SP3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the feature council a real, per-product runtime — add an `azure`
service mode, scope each sweep to a single product, ship an orchestrator runtime
image, render a per-product runtime bundle, and un-defer `deploy_council` so
`dsf new --execute` brings the council up for the product.

**Architecture:** `build_services('azure')` resolves `AzureRuntimeSettings`
from the environment (only `DSF_PRODUCT` required), wires the real GitHub client +
the existing OTel tracer, and carries `product`/`azure` on the `Services` bundle
(model/memory/config stay on fakes behind a clear seam — real Azure adapters are
SP3b). The scheduler stamps `Run.product` from the bundle. `dsf new` renders a
`compose.orchestrator.yml` + `.env.orchestrator` from the Azure deployment outputs
and, on `--execute` with `runtime_target=homelab`, runs `docker compose up -d`
through the injectable runner (aca raises a clear deferral). All tests stay
offline.

**Tech Stack:** Python 3.12, pydantic v2, argparse CLI (`python -m dsf.cli`),
ruff (line-length 100), pytest (`asyncio_mode=auto`, `pythonpath=["src"]`),
injectable `subprocess.run`/`MagicMock` runner pattern, Docker (rendered compose).

**Source of truth:** `docs/superpowers/specs/2026-06-17-dsf-feature-council-runtime-productionization-design.md`

**Conventions to follow (verified in-repo):**
- First line of every module: `from __future__ import annotations`.
- Tests mirror `src/` under `tests/` (e.g. `src/dsf/instance/runtime_render.py`
  → `tests/instance/test_runtime_render.py`).
- The global `--mode` flag MUST precede the subcommand:
  `python -m dsf.cli --mode azure serve-orchestrator` (putting `--mode` after the
  subcommand yields "unrecognized arguments").
- Commit each task with `git -c commit.gpgsign=false commit` and the trailer
  `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
- Validate after each task: `uv run ruff check .` and the named pytest selection;
  run the full suite + `uv run python -m dsf.evals.runner --gate` at the end.

---

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `src/dsf/container.py` | `AzureRuntimeSettings`, `Services.product/azure`, `build_services('azure')` | Modify |
| `tests/test_container.py` | container/CLI tests; update 2 azure-assuming tests + add azure tests | Modify |
| `src/dsf/cli.py` | `_get_services` also exits cleanly on `ValueError` (azure missing `DSF_PRODUCT`) | Modify |
| `src/dsf/triggers/scheduler.py` | stamp `Run.product` from `services.product` | Modify |
| `tests/triggers/test_scheduler.py` | add scoping tests | Modify |
| `src/dsf/runtime/__init__.py` | new package marker/docstring for the orchestrator runtime image | Create |
| `src/dsf/runtime/Dockerfile` | two-stage, non-root orchestrator image; `CMD` runs azure-mode sweep worker | Create |
| `tests/runtime/test_runtime_image.py` | assert Dockerfile shape + correct `--mode azure` CMD ordering | Create |
| `src/dsf/instance/runtime_render.py` | `render_runtime_bundle` → `compose.orchestrator.yml` + `.env.orchestrator` | Create |
| `tests/instance/test_runtime_render.py` | render tests (endpoints mapped, product scoped, no secrets inlined) | Create |
| `src/dsf/instance/provisioner.py` | un-defer `deploy_council`: render always; homelab bring-up on execute; aca raises | Modify |
| `tests/instance/test_provisioner.py` | update deferred-set + deploy_council result assertions; add bring-up test | Modify |
| `.gitignore` | ignore generated `config/instances/*.runtime/` bundles | Modify |
| `README.md`, `docs/RUNBOOK.md`, `infra/README.md`, charter spec, ADR 0002 | documentation sweep | Modify |

---

## Task 1: `AzureRuntimeSettings` + `Services` azure fields

**Files:**
- Modify: `src/dsf/container.py`
- Test: `tests/test_container.py`

- [ ] **Step 1: Write the failing tests**

Add to `tests/test_container.py` (imports + new tests):

```python
# add to the existing imports near the top of the file
from collections.abc import Mapping  # noqa: F401  (used by type-readers)

from dsf.container import AzureRuntimeSettings


def test_azure_runtime_settings_from_env_requires_product():
    import pytest
    with pytest.raises(ValueError):
        AzureRuntimeSettings.from_env({})
    with pytest.raises(ValueError):
        AzureRuntimeSettings.from_env({"DSF_PRODUCT": "   "})


def test_azure_runtime_settings_from_env_reads_endpoints():
    settings = AzureRuntimeSettings.from_env(
        {
            "DSF_PRODUCT": "microbi",
            "AZURE_APPCONFIG_ENDPOINT": "https://ac.example",
            "AZURE_KEYVAULT_URI": "https://kv.example",
            "APPLICATIONINSIGHTS_CONNECTION_STRING": "InstrumentationKey=abc",
            "AZURE_COSMOS_ENDPOINT": "https://cosmos.example",
        }
    )
    assert settings.product == "microbi"
    assert settings.appconfig_endpoint == "https://ac.example"
    assert settings.keyvault_uri == "https://kv.example"
    assert settings.appinsights_connection_string == "InstrumentationKey=abc"
    assert settings.cosmos_endpoint == "https://cosmos.example"


def test_azure_runtime_settings_endpoints_optional():
    settings = AzureRuntimeSettings.from_env({"DSF_PRODUCT": "microbi"})
    assert settings.product == "microbi"
    assert settings.appconfig_endpoint == ""
    assert settings.cosmos_endpoint == ""


def test_services_has_product_and_azure_defaults_none():
    services = build_services("local")
    assert services.product is None
    assert services.azure is None
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest tests/test_container.py -k "azure_runtime_settings or product_and_azure" -q`
Expected: FAIL — `ImportError: cannot import name 'AzureRuntimeSettings'`.

- [ ] **Step 3: Implement `AzureRuntimeSettings` + `Services` fields**

In `src/dsf/container.py`, replace the import block + `Services` dataclass region
(lines 3-32) with:

```python
from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass

from pydantic import BaseModel

from dsf.fakes import (
    FakeConfigStore,
    FakeGitHubClient,
    FakeMemoryStore,
    FakeModelClient,
    FakeTracer,
)
from dsf.ports import (
    ConfigStore,
    GitHubClient,
    MemoryStore,
    ModelClient,
    Tracer,
)


class AzureRuntimeSettings(BaseModel):
    """Runtime configuration for ``azure`` mode, resolved from the environment.

    Only ``product`` (``DSF_PRODUCT``) is required — it scopes the factory to a
    single product. The endpoints are optional: they are carried for the real
    service adapters (Cosmos/App Config/App Insights) that land in SP3b, and are
    rendered into the per-product runtime bundle today.
    """

    product: str
    appconfig_endpoint: str = ""
    keyvault_uri: str = ""
    appinsights_connection_string: str = ""
    cosmos_endpoint: str = ""

    @classmethod
    def from_env(cls, env: Mapping[str, str]) -> "AzureRuntimeSettings":
        """Resolve settings from ``env``. Raises ``ValueError`` if ``DSF_PRODUCT``
        is missing or blank — azure mode is meaningless without a product scope."""
        product = (env.get("DSF_PRODUCT") or "").strip()
        if not product:
            raise ValueError(
                "azure mode requires DSF_PRODUCT to scope the factory runtime "
                "(set DSF_PRODUCT=<product>)."
            )
        return cls(
            product=product,
            appconfig_endpoint=(env.get("AZURE_APPCONFIG_ENDPOINT") or "").strip(),
            keyvault_uri=(env.get("AZURE_KEYVAULT_URI") or "").strip(),
            appinsights_connection_string=(
                env.get("APPLICATIONINSIGHTS_CONNECTION_STRING") or ""
            ).strip(),
            cosmos_endpoint=(env.get("AZURE_COSMOS_ENDPOINT") or "").strip(),
        )


@dataclass
class Services:
    """Bundle of every port instance, selected per mode."""

    mode: str
    model: ModelClient
    memory: MemoryStore
    config: ConfigStore
    github: GitHubClient
    tracer: Tracer
    product: str | None = None
    azure: AzureRuntimeSettings | None = None
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `uv run pytest tests/test_container.py -k "azure_runtime_settings or product_and_azure" -q`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/dsf/container.py tests/test_container.py
git -c commit.gpgsign=false commit -m "feat(container): add AzureRuntimeSettings + Services product/azure fields

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 2: `build_services('azure')` (no longer raises)

**Files:**
- Modify: `src/dsf/container.py`
- Modify: `src/dsf/cli.py:27-33`
- Test: `tests/test_container.py:51-53`, `tests/test_container.py:95-103`

- [ ] **Step 1: Update the two azure-assuming tests, then add azure-mode tests**

In `tests/test_container.py`, change `test_build_services_unknown_mode_raises`
(currently asserting `build_services("azure")` raises) to use a still-unknown mode:

```python
def test_build_services_unknown_mode_raises():
    with pytest.raises(NotImplementedError):
        build_services("gcp")
```

Change `test_cli_unsupported_mode_exits_cleanly` (currently `--mode azure`) to use
`gcp` as well:

```python
def test_cli_unsupported_mode_exits_cleanly(capsys):
    """An unsupported --mode must exit non-zero with a clear message, no traceback."""
    with pytest.raises(SystemExit) as exc_info:
        main(["--mode", "gcp", "sweep"])
    assert exc_info.value.code == 1
    err = capsys.readouterr().err
    assert "not yet supported" in err
    assert "gcp" in err
```

Now add new azure-mode tests:

```python
def test_build_services_azure_wires_real_github_and_settings():
    from dsf.github_client import RealGitHubClient

    services = build_services("azure", env={"DSF_PRODUCT": "microbi"})
    assert services.mode == "azure"
    assert isinstance(services.github, RealGitHubClient)
    assert services.product == "microbi"
    assert services.azure is not None
    assert services.azure.product == "microbi"
    # model/memory/config remain fakes (the deferred-adapter seam):
    assert isinstance(services.model, FakeModelClient)
    assert isinstance(services.memory, FakeMemoryStore)
    assert isinstance(services.config, FakeConfigStore)
    # tracer comes from build_tracer("azure") and still satisfies the port:
    assert isinstance(services.tracer, Tracer)


def test_build_services_azure_missing_product_raises_value_error():
    with pytest.raises(ValueError):
        build_services("azure", env={})


def test_cli_azure_mode_without_product_exits_cleanly(capsys, monkeypatch):
    monkeypatch.delenv("DSF_PRODUCT", raising=False)
    with pytest.raises(SystemExit) as exc_info:
        main(["--mode", "azure", "sweep"])
    assert exc_info.value.code == 1
    assert "DSF_PRODUCT" in capsys.readouterr().err
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest tests/test_container.py -k "azure or unsupported_mode or unknown_mode" -q`
Expected: FAIL — `build_services()` has no `env` kwarg / azure still raises
`NotImplementedError`.

- [ ] **Step 3: Implement `build_services('azure')`**

In `src/dsf/container.py`, replace the `build_services` signature + the trailing
`raise NotImplementedError` (the function starting at `def build_services` through
its final `raise`) with:

```python
def build_services(
    mode: str = "local", *, env: Mapping[str, str] | None = None
) -> Services:
    """Build a wired :class:`Services` bundle.

    Supported modes
    ---------------
    ``local``
        Fully in-memory fakes — deterministic, no network calls, no credentials
        required. All filing is dry-run unless explicitly overridden per-run.
    ``gh``
        Same fakes for model/memory/config/tracer, but with a real
        :class:`~dsf.github_client.RealGitHubClient` that calls the ``gh`` CLI.
        Requires ``gh`` to be authenticated in the environment.
    ``azure``
        Per-product runtime mode. Resolves :class:`AzureRuntimeSettings` from
        ``env`` (defaults to ``os.environ``; only ``DSF_PRODUCT`` is required),
        wires the real GitHub client and the OpenTelemetry tracer
        (:func:`dsf.observability.tracing.build_tracer`, which degrades to the
        fake tracer when OpenTelemetry is not installed), and keeps
        model/memory/config on fakes behind the deferred-adapter seam (SP3b).
        The resolved ``product``/``azure`` settings are carried on the bundle.
    """
    if mode == "local":
        return Services(
            mode=mode,
            model=FakeModelClient(),
            memory=FakeMemoryStore(),
            config=FakeConfigStore.from_defaults(),
            github=FakeGitHubClient(),
            tracer=FakeTracer(),
        )
    if mode == "gh":
        from dsf.github_client import RealGitHubClient

        return Services(
            mode=mode,
            model=FakeModelClient(),
            memory=FakeMemoryStore(),
            config=FakeConfigStore.from_defaults(),
            github=RealGitHubClient(),
            tracer=FakeTracer(),
        )
    if mode == "azure":
        from dsf.github_client import RealGitHubClient
        from dsf.observability.tracing import build_tracer

        settings = AzureRuntimeSettings.from_env(env if env is not None else os.environ)
        return Services(
            mode=mode,
            model=FakeModelClient(),
            memory=FakeMemoryStore(),
            config=FakeConfigStore.from_defaults(),
            github=RealGitHubClient(),
            tracer=build_tracer("azure"),
            product=settings.product,
            azure=settings,
        )
    raise NotImplementedError(
        f"mode {mode!r} is not yet supported (available: 'local', 'gh', 'azure')."
    )
```

- [ ] **Step 4: Let the CLI exit cleanly when azure settings are missing**

In `src/dsf/cli.py`, widen the caught exception in `_get_services` (lines 27-33)
so a missing `DSF_PRODUCT` (a `ValueError`) exits cleanly instead of a traceback:

```python
def _get_services(mode: str):
    """Build a services bundle or exit cleanly on unsupported/misconfigured modes."""
    try:
        return build_services(mode)
    except (NotImplementedError, ValueError) as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        sys.exit(1)
```

Also update the `--mode` help text (cli.py ~lines 160-165) to mention azure:

```python
        help=(
            "service mode: 'local' (in-memory fakes, default), 'gh' (real GitHub "
            "client via gh CLI), or 'azure' (per-product runtime; requires "
            "DSF_PRODUCT). Other modes are not yet supported."
        ),
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `uv run pytest tests/test_container.py -q`
Expected: PASS (all container tests, including the new azure ones).

- [ ] **Step 6: Commit**

```bash
git add src/dsf/container.py src/dsf/cli.py tests/test_container.py
git -c commit.gpgsign=false commit -m "feat(container): build_services('azure') wires real github + otel tracer

azure mode resolves AzureRuntimeSettings (DSF_PRODUCT required), keeps
model/memory/config on fakes behind the SP3b adapter seam. CLI exits cleanly
when DSF_PRODUCT is missing.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 3: Single-product scoping in the scheduled sweep

**Files:**
- Modify: `src/dsf/triggers/scheduler.py`
- Test: `tests/triggers/test_scheduler.py`

- [ ] **Step 1: Write the failing tests**

Add to `tests/triggers/test_scheduler.py`:

```python
async def test_sweep_scopes_run_to_services_product():
    from dsf.container import build_services
    from dsf.triggers.scheduler import sweep

    services = build_services("local")
    services.product = "microbi"
    run = await sweep(services)
    assert run.product == "microbi"


async def test_sweep_unscoped_when_no_product():
    from dsf.container import build_services
    from dsf.triggers.scheduler import sweep

    services = build_services("local")
    run = await sweep(services)
    assert run.product is None
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest tests/triggers/test_scheduler.py -k "scopes_run_to_services_product or unscoped_when_no_product" -q`
Expected: FAIL — `run.product` is `None` even when `services.product` is set.

- [ ] **Step 3: Stamp `Run.product` from the bundle**

In `src/dsf/triggers/scheduler.py`, replace the `run = Run(...)` construction
inside `sweep` (currently line 53) with a product-scoped one:

```python
    source_kinds = _enabled_source_kinds(services)
    run = Run(
        trigger=TriggerKind.SCHEDULED,
        source_kinds=source_kinds,
        product=services.product,
    )
```

`Run.product` defaults to `None` (`src/dsf/contracts/models.py:100`), so when
`services.product` is `None` (local/gh) the run is unscoped exactly as before.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `uv run pytest tests/triggers/test_scheduler.py -q`
Expected: PASS (existing + 2 new scheduler tests).

- [ ] **Step 5: Commit**

```bash
git add src/dsf/triggers/scheduler.py tests/triggers/test_scheduler.py
git -c commit.gpgsign=false commit -m "feat(scheduler): scope scheduled sweep run to services.product

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 4: Orchestrator runtime image

**Files:**
- Create: `src/dsf/runtime/__init__.py`
- Create: `src/dsf/runtime/Dockerfile`
- Test: `tests/runtime/test_runtime_image.py`

- [ ] **Step 1: Write the failing test**

Create `tests/runtime/__init__.py` (empty) and `tests/runtime/test_runtime_image.py`:

```python
"""The orchestrator runtime image mirrors the agent Dockerfile pattern."""

from __future__ import annotations

from pathlib import Path

DOCKERFILE = Path(__file__).resolve().parents[2] / "src" / "dsf" / "runtime" / "Dockerfile"


def test_runtime_dockerfile_exists():
    assert DOCKERFILE.is_file()


def test_runtime_dockerfile_is_two_stage_nonroot_pinned():
    text = DOCKERFILE.read_text(encoding="utf-8")
    # two-stage build on a digest-pinned slim base:
    assert "AS builder" in text
    assert "python:3.12-slim@sha256:" in text
    # runs as the non-root appuser (uid 1001), like the agent images:
    assert "USER appuser" in text
    assert "--uid 1001" in text


def test_runtime_dockerfile_cmd_runs_azure_sweep_worker():
    text = DOCKERFILE.read_text(encoding="utf-8")
    # the global --mode flag MUST precede the subcommand:
    assert (
        'CMD ["python", "-m", "dsf.cli", "--mode", "azure", "serve-orchestrator"]'
        in text
    )
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest tests/runtime/test_runtime_image.py -q`
Expected: FAIL — `Dockerfile` does not exist.

- [ ] **Step 3: Create the runtime package + Dockerfile**

Create `src/dsf/runtime/__init__.py`:

```python
"""Orchestrator runtime image package.

The feature-council orchestrator runs as a long-lived worker in the product's
runtime target (homelab docker compose today; ACA later). The image is built from
the sibling ``Dockerfile`` and started via ``dsf --mode azure serve-orchestrator``.
"""

from __future__ import annotations
```

Create `src/dsf/runtime/Dockerfile` (mirrors `src/dsf/agents/sentry/Dockerfile`,
same digest-pinned base + non-root user; the CMD runs the azure-mode sweep worker
with `--mode` BEFORE the subcommand):

```dockerfile
FROM python:3.12-slim@sha256:d764629ce0ddd8c71fd371e9901efb324a95789d2315a47db7e4d27e78f1b0e9 AS builder

WORKDIR /build

COPY pyproject.toml uv.lock ./
COPY src/ ./src/

RUN pip install --no-cache-dir --prefix=/install .

FROM python:3.12-slim@sha256:d764629ce0ddd8c71fd371e9901efb324a95789d2315a47db7e4d27e78f1b0e9

RUN groupadd --system --gid 1001 appgroup \
    && useradd --system --uid 1001 --gid 1001 --no-create-home --gid appgroup appuser

WORKDIR /app

COPY --from=builder /install /usr/local

USER appuser

# Per-product feature-council orchestrator. Scope is supplied at runtime via
# DSF_PRODUCT (see .env.orchestrator). The global --mode flag MUST precede the
# subcommand.
CMD ["python", "-m", "dsf.cli", "--mode", "azure", "serve-orchestrator"]
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `uv run pytest tests/runtime/test_runtime_image.py -q`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/dsf/runtime/__init__.py src/dsf/runtime/Dockerfile tests/runtime/
git -c commit.gpgsign=false commit -m "feat(runtime): add orchestrator runtime Dockerfile (azure-mode sweep worker)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 5: Render the per-product runtime bundle

**Files:**
- Create: `src/dsf/instance/runtime_render.py`
- Modify: `.gitignore`
- Test: `tests/instance/test_runtime_render.py`

- [ ] **Step 1: Write the failing tests**

Create `tests/instance/test_runtime_render.py`:

```python
"""Tests for render_runtime_bundle (per-product runtime scaffolding)."""

from __future__ import annotations

from dsf.instance.runtime_render import render_runtime_bundle, runtime_dir
from dsf.instance.spec import AzureProvisionResult, InstanceManifest, InstanceSpec
from dsf.instance.provisioner import InstanceProvisioner


def _manifest(tmp_path, *, with_azure: bool = True) -> InstanceManifest:
    spec = InstanceSpec(product="microbi", owner="acme", name_prefix="microbi")
    plan = InstanceProvisioner(spec, repo_root=tmp_path).plan()
    azure = (
        AzureProvisionResult(
            resource_group="rg-dsf-microbi",
            deployment_name="dsf-microbi",
            location="swedencentral",
            outputs={
                "appConfigEndpoint": "https://ac.example",
                "keyVaultUri": "https://kv.example",
                "appInsightsConnectionString": "InstrumentationKey=abc;IngestionEndpoint=https://i.example",
                "cosmosEndpoint": "https://cosmos.example",
            },
        )
        if with_azure
        else None
    )
    return InstanceManifest(spec=spec, plan=plan, executed=with_azure, azure=azure)


def test_render_writes_compose_and_env_under_runtime_dir(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    assert bundle.runtime_dir == runtime_dir("microbi", tmp_path)
    assert bundle.runtime_dir == tmp_path / "config" / "instances" / "microbi.runtime"
    assert bundle.compose_path.is_file()
    assert bundle.env_path.is_file()
    assert bundle.compose_path.name == "compose.orchestrator.yml"
    assert bundle.env_path.name == ".env.orchestrator"


def test_render_env_scopes_product_and_maps_endpoints(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    env = bundle.env_path.read_text(encoding="utf-8")
    assert "DSF_MODE=azure" in env
    assert "DSF_PRODUCT=microbi" in env
    assert "AZURE_APPCONFIG_ENDPOINT=https://ac.example" in env
    assert "AZURE_KEYVAULT_URI=https://kv.example" in env
    assert "AZURE_COSMOS_ENDPOINT=https://cosmos.example" in env
    assert "APPLICATIONINSIGHTS_CONNECTION_STRING=InstrumentationKey=abc" in env


def test_render_compose_scopes_container_and_references_env_file(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    compose = bundle.compose_path.read_text(encoding="utf-8")
    assert "dsf-orchestrator-microbi" in compose
    assert ".env.orchestrator" in compose
    assert "src/dsf/runtime/Dockerfile" in compose


def test_render_does_not_inline_secrets(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    env = bundle.env_path.read_text(encoding="utf-8")
    # bearer/GitHub tokens are runtime-injected (Key Vault / managed identity),
    # never rendered into the bundle:
    assert "A2A_BEARER_TOKEN=" not in env or "A2A_BEARER_TOKEN=\n" in env
    assert "GITHUB_TOKEN" not in env
    assert "GH_TOKEN" not in env


def test_render_tolerates_missing_azure_outputs(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path, with_azure=False), repo_root=tmp_path)
    env = bundle.env_path.read_text(encoding="utf-8")
    assert "DSF_PRODUCT=microbi" in env
    # endpoints are blank (not yet provisioned) but the keys are present:
    assert "AZURE_APPCONFIG_ENDPOINT=" in env
    assert "AZURE_COSMOS_ENDPOINT=" in env
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest tests/instance/test_runtime_render.py -q`
Expected: FAIL — `ModuleNotFoundError: dsf.instance.runtime_render`.

- [ ] **Step 3: Implement `render_runtime_bundle`**

Create `src/dsf/instance/runtime_render.py`:

```python
"""Render the per-product feature-council runtime bundle.

For an :class:`~dsf.instance.spec.InstanceManifest`, write a
``compose.orchestrator.yml`` + ``.env.orchestrator`` pair into
``config/instances/<product>.runtime/``. The env file scopes the runtime to the
product (``DSF_PRODUCT``) and carries the Azure backing-service endpoints captured
in the deployment outputs. Secrets (bearer tokens, GitHub tokens) are NOT
rendered — they are injected at runtime from Key Vault / managed identity.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from dsf.instance.spec import InstanceManifest, instances_dir

#: Mapping of Bicep deployment output keys -> the env var names the runtime reads
#: (kept symmetric with :meth:`dsf.container.AzureRuntimeSettings.from_env`).
_ENDPOINT_MAP: tuple[tuple[str, str], ...] = (
    ("AZURE_APPCONFIG_ENDPOINT", "appConfigEndpoint"),
    ("AZURE_KEYVAULT_URI", "keyVaultUri"),
    ("APPLICATIONINSIGHTS_CONNECTION_STRING", "appInsightsConnectionString"),
    ("AZURE_COSMOS_ENDPOINT", "cosmosEndpoint"),
)


@dataclass(frozen=True)
class RuntimeBundle:
    """Paths to the rendered runtime files for one product."""

    runtime_dir: Path
    compose_path: Path
    env_path: Path


def runtime_dir(product: str, repo_root: Path | None = None) -> Path:
    """Directory holding a product's generated runtime bundle."""
    return instances_dir(repo_root) / f"{product}.runtime"


def _render_env(product: str, outputs: dict[str, str]) -> str:
    lines = [
        "# .env.orchestrator — GENERATED by dsf (render_runtime_bundle). Do not edit.",
        "# Endpoints come from the product's Azure deployment outputs. Secrets are",
        "# injected at runtime (Key Vault / managed identity), never rendered here.",
        "DSF_MODE=azure",
        f"DSF_PRODUCT={product}",
    ]
    lines.extend(f"{var}={outputs.get(key, '')}" for var, key in _ENDPOINT_MAP)
    return "\n".join(lines) + "\n"


def _render_compose(product: str, resource_group: str) -> str:
    return (
        "# compose.orchestrator.yml — GENERATED by dsf (render_runtime_bundle).\n"
        f"# Feature-council runtime for product '{product}'. Backing services live\n"
        f"# in Azure RG '{resource_group}' and are reached OUTBOUND (see ADR 0002).\n"
        "#\n"
        "# Usage (homelab):\n"
        "#   docker compose -f compose.orchestrator.yml --env-file .env.orchestrator up -d\n"
        "services:\n"
        "  orchestrator:\n"
        "    # Built from the orchestrator runtime image. Build context is the REPO\n"
        "    # ROOT so `pip install .` resolves the dsf package.\n"
        "    build:\n"
        "      context: ../../..\n"
        "      dockerfile: src/dsf/runtime/Dockerfile\n"
        "    image: ${DSF_RUNTIME_IMAGE:-dsf/runtime:local}\n"
        f"    container_name: dsf-orchestrator-{product}\n"
        "    restart: unless-stopped\n"
        "    env_file:\n"
        "      - .env.orchestrator\n"
        "    networks:\n"
        "      - dsf\n"
        "networks:\n"
        "  dsf:\n"
        "    driver: bridge\n"
    )


def render_runtime_bundle(
    manifest: InstanceManifest, *, repo_root: Path | None = None
) -> RuntimeBundle:
    """Render ``compose.orchestrator.yml`` + ``.env.orchestrator`` for ``manifest``.

    Tolerates a manifest with no Azure outputs yet (endpoints render blank).
    """
    product = manifest.spec.product
    outputs = manifest.azure.outputs if manifest.azure else {}
    rdir = runtime_dir(product, repo_root)
    rdir.mkdir(parents=True, exist_ok=True)
    env_path = rdir / ".env.orchestrator"
    compose_path = rdir / "compose.orchestrator.yml"
    env_path.write_text(_render_env(product, outputs), encoding="utf-8")
    compose_path.write_text(
        _render_compose(product, manifest.spec.resource_group()), encoding="utf-8"
    )
    return RuntimeBundle(runtime_dir=rdir, compose_path=compose_path, env_path=env_path)


__all__ = ["RuntimeBundle", "render_runtime_bundle", "runtime_dir"]
```

- [ ] **Step 4: Ignore generated runtime bundles**

Append to `.gitignore`:

```gitignore
config/instances/*.runtime/
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `uv run pytest tests/instance/test_runtime_render.py -q`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/dsf/instance/runtime_render.py tests/instance/test_runtime_render.py .gitignore
git -c commit.gpgsign=false commit -m "feat(instance): render per-product runtime bundle (compose + env)

Writes config/instances/<product>.runtime/{compose.orchestrator.yml,.env.orchestrator}
from the Azure deployment outputs; secrets are runtime-injected, not inlined.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 6: Un-defer `deploy_council` in the provisioner

**Files:**
- Modify: `src/dsf/instance/provisioner.py`
- Test: `tests/instance/test_provisioner.py:33-36`, `:94`, `:123`, plus new bring-up test

- [ ] **Step 1: Update existing assertions + write the new bring-up tests**

In `tests/instance/test_provisioner.py`:

1. `test_plan_deferred_flags` (line 33-36) — only `deploy_sre` stays deferred now:

```python
def test_plan_deferred_flags():
    plan = InstanceProvisioner(_spec()).plan()
    deferred = {s.name for s in plan.steps if s.deferred}
    assert deferred == {"deploy_sre"}
```

2. `test_apply_dry_run_writes_manifest_and_runs_nothing` (line 94) — `deploy_council`
   now renders during a dry-run:

```python
    assert results["deploy_council"] == "rendered (dry-run)"
```

3. `test_apply_execute_runs_real_steps_and_stubs_deferred` (line 123) — under
   execute with the default `homelab` target, `deploy_council` brings the runtime
   up:

```python
    assert results["deploy_council"] == "deployed"
```

Then add two new tests at the end of the file:

```python
def test_apply_execute_homelab_brings_up_runtime(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        returncode = 1 if cmd[:3] == ["gh", "repo", "view"] else 0
        return MagicMock(returncode=returncode, stdout="{}")

    spec = InstanceSpec(product="demo", owner="acme")  # runtime_target defaults to homelab
    prov = InstanceProvisioner(spec, run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    compose = tmp_path / "config" / "instances" / "demo.runtime" / "compose.orchestrator.yml"
    env = tmp_path / "config" / "instances" / "demo.runtime" / ".env.orchestrator"
    up = next(c for c in calls if c[:3] == ["docker", "compose", "-f"])
    assert str(compose) in up
    assert "--env-file" in up and str(env) in up
    assert up[-2:] == ["up", "-d"]
    assert {s.name: s.result for s in manifest.plan.steps}["deploy_council"] == "deployed"


def test_apply_execute_aca_target_raises(tmp_path):
    def fake_run(cmd, **kwargs):
        returncode = 1 if cmd[:3] == ["gh", "repo", "view"] else 0
        return MagicMock(returncode=returncode, stdout="{}")

    spec = InstanceSpec(product="demo", owner="acme", runtime_target="aca")
    prov = InstanceProvisioner(spec, run=fake_run, repo_root=tmp_path)
    with pytest.raises(NotImplementedError, match="aca"):
        prov.apply(execute=True)
    # manifest is still persisted (try/finally) so the prefix survives:
    assert (tmp_path / "config" / "instances" / "demo.json").exists()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest tests/instance/test_provisioner.py -q`
Expected: FAIL — `deploy_council` still flagged deferred / result is `"deferred"`;
new bring-up tests find no docker call.

- [ ] **Step 3: Un-defer `deploy_council` in `plan()`**

In `src/dsf/instance/provisioner.py`, replace the `deploy_council` step in `plan()`
(lines 108-114) with an active (non-deferred) step that carries the product scope:

```python
            ProvisionStep(
                name="deploy_council",
                description=(
                    f"Render + bring up the feature-council runtime scoped to {s.product}"
                ),
            ),
```

- [ ] **Step 4: Handle `deploy_council` in `apply()`**

In `src/dsf/instance/provisioner.py`, add the render/bring-up branch to the step
loop in `apply()`. It MUST come right after the `if step.deferred:` check (so it
renders even on a dry-run, before the generic `not execute` / `not step.command`
branches). Replace the loop body opening (lines 144-152, from `for step in plan.steps:`
through the `elif not step.command:` branch) with:

```python
            for step in plan.steps:
                if step.name == "write_config":
                    continue  # finalized after the manifest is built
                if step.deferred:
                    step.result = "deferred"
                elif step.name == "deploy_council":
                    provisional = InstanceManifest(
                        spec=self.spec, plan=plan, executed=executed, azure=azure_result
                    )
                    bundle = render_runtime_bundle(provisional, repo_root=self._repo_root)
                    if not execute:
                        step.result = "rendered (dry-run)"
                    elif self.spec.runtime_target == "homelab":
                        self._run(
                            [
                                "docker", "compose",
                                "-f", str(bundle.compose_path),
                                "--env-file", str(bundle.env_path),
                                "up", "-d",
                            ],
                            check=True,
                        )
                        step.executed, step.result = True, "deployed"
                    else:
                        raise NotImplementedError(
                            f"runtime_target {self.spec.runtime_target!r} bring-up is "
                            "not yet implemented (homelab only in SP3)."
                        )
                elif not execute:
                    step.result = "dry-run"
                elif not step.command:
                    step.result = "noop"
```

Add the import at the top of `src/dsf/instance/provisioner.py` (after the existing
`from dsf.instance.spec import (...)` block):

```python
from dsf.instance.runtime_render import render_runtime_bundle
```

> Note: `azure_result` is already assigned before the loop and updated by the
> `provision_azure` branch, which runs *before* `deploy_council` in step order, so
> the provisional manifest sees freshly-captured outputs when executing and the
> carried-forward outputs otherwise.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `uv run pytest tests/instance/test_provisioner.py -q`
Expected: PASS (existing + 2 new provisioner tests).

- [ ] **Step 6: Run the instance + CLI suites to confirm no regressions**

Run: `uv run pytest tests/instance tests/test_container.py tests/triggers/test_scheduler.py -q`
Expected: PASS. (`tests/instance/test_cli_new.py:39` `assert "deferred" in out`
still holds — `deploy_sre` remains deferred.)

- [ ] **Step 7: Commit**

```bash
git add src/dsf/instance/provisioner.py tests/instance/test_provisioner.py
git -c commit.gpgsign=false commit -m "feat(instance): un-defer deploy_council — render + homelab bring-up

deploy_council renders the runtime bundle on every apply; under --execute with
runtime_target=homelab it runs 'docker compose up -d' via the injected runner;
aca raises a clear deferral. deploy_sre stays deferred (SP5).

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 7: Documentation sweep

The owner asked for a **full sweep** of the docs (much is outdated). Update each
file below, then verify. (The separate `refactor-cli-runtime-split` — splitting the
provisioning CLI from the feature-council runtime source — is tracked as its own
future sub-project and is **out of scope here**.)

**Files:** `README.md`, `docs/RUNBOOK.md`, `infra/README.md`,
`docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md`,
`docs/adr/0002-homelab-runtime-azure-backing-only.md`.

- [ ] **Step 1: README — document azure mode + the per-product runtime**

In `README.md`, wherever service modes are listed (search for `--mode`, `gh`,
`local`), add `azure`:

```markdown
- `--mode azure` — per-product runtime mode. Resolves runtime settings from the
  environment (`DSF_PRODUCT` required; `AZURE_APPCONFIG_ENDPOINT`,
  `AZURE_KEYVAULT_URI`, `APPLICATIONINSIGHTS_CONNECTION_STRING`,
  `AZURE_COSMOS_ENDPOINT` optional). Wires the real GitHub client and the
  OpenTelemetry tracer; model/memory/config remain fakes until the Azure service
  adapters land (SP3b). The global `--mode` flag must precede the subcommand:
  `python -m dsf.cli --mode azure serve-orchestrator`.
```

In the `dsf new` section, note that `--execute` now also brings up the council
runtime for `homelab` targets:

```markdown
Under `--execute` with `--runtime-target homelab`, `dsf new` renders
`config/instances/<product>.runtime/{compose.orchestrator.yml,.env.orchestrator}`
from the Azure deployment outputs and runs `docker compose up -d` to start the
product's feature-council orchestrator. `--runtime-target aca` is not yet
implemented (raises a clear error). The SRE agent step stays deferred (SP5).
```

- [ ] **Step 2: RUNBOOK — add the council bring-up + scoping operations**

In `docs/RUNBOOK.md`, add a subsection after the Azure provisioning steps:

```markdown
### Per-product feature-council runtime

`dsf new --execute` (homelab) renders and starts the product's orchestrator:

```bash
# 1. Provision + render + bring up (homelab):
python -m dsf.cli new --product microbi --owner your-org \
  --name-prefix microbi --execute

# 2. Inspect / restart the rendered runtime directly:
cd config/instances/microbi.runtime
docker compose -f compose.orchestrator.yml --env-file .env.orchestrator up -d
docker compose -f compose.orchestrator.yml logs -f
```

The rendered `.env.orchestrator` carries `DSF_PRODUCT` (scope) and the Azure
endpoint outputs. Secrets (A2A bearer, GitHub token) are NOT in the file — inject
them at runtime via Key Vault / managed identity. The bundle directory is
git-ignored.

Run a one-shot scoped sweep manually:

```bash
DSF_PRODUCT=microbi python -m dsf.cli --mode azure serve-orchestrator
```
```

- [ ] **Step 3: infra/README — cross-link the runtime image + bundle**

In `infra/README.md`, under the "No compute" note, add:

```markdown
The **feature-council orchestrator** runs in your runtime target, not in Azure.
`dsf new` builds it from `src/dsf/runtime/Dockerfile` and renders a per-product
`config/instances/<product>.runtime/` compose bundle that reaches these backing
services outbound (mirrors `infra/compose.homelab.yml`).
```

- [ ] **Step 4: Charter — mark SP3 status + note SP3b**

In `docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md`,
update the roadmap/decomposition row for SP3 to reflect completion and the
deferred adapter follow-up:

```markdown
- **SP3 (done):** council `azure` mode + per-product scoping + orchestrator runtime
  image + rendered runtime bundle + homelab bring-up. **SP3b (follow-up):** real
  Azure service adapters (App Configuration → config, Cosmos → memory, LLM →
  model), each behind the seam established in SP3. (The App Insights/OTel tracer
  is already wired in SP3.)
```

- [ ] **Step 5: ADR 0002 — confirm runtime bring-up matches the homelab decision**

In `docs/adr/0002-homelab-runtime-azure-backing-only.md`, add a short note in the
Consequences/Status section confirming SP3 realizes the decision:

```markdown
> **SP3 update:** the per-product feature-council orchestrator is rendered as a
> homelab `docker compose` bundle (`config/instances/<product>.runtime/`) that
> reaches the Azure backing services outbound. No compute is added to Bicep; the
> `aca` runtime target remains an explicit, unimplemented seam.
```

- [ ] **Step 6: Skim every doc for stale claims, then verify the whole repo**

Search the docs for now-stale statements and fix any that say azure mode is
unsupported or that the council can't be deployed:

Run: `grep -rn "not yet supported\|NotImplementedError\|deferred to SP3\|azure.*raises" README.md docs/ infra/README.md`
Fix any prose that contradicts the shipped SP3 behavior (azure mode works;
`deploy_council` is active; only `deploy_sre`/`aca` remain deferred).

Then run the full validation gate:

Run: `uv run ruff check . && uv run pytest -q && uv run python -m dsf.evals.runner --gate`
Expected: ruff clean; full suite PASS; evals gate PASSED.

- [ ] **Step 7: Commit**

```bash
git add README.md docs/ infra/README.md
git -c commit.gpgsign=false commit -m "docs: sweep for SP3 — azure mode, per-product council runtime, bring-up

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Done When

- `ruff` clean; full suite passes (existing 281 + the new SP3 tests); evals gate PASSED.
- `build_services('azure', env={'DSF_PRODUCT': ...})` returns a bundle with the real
  GitHub client, the OTel tracer, and `product`/`azure` populated; missing
  `DSF_PRODUCT` exits the CLI cleanly.
- A scheduled sweep stamps `Run.product` from `services.product`.
- `src/dsf/runtime/Dockerfile` exists (two-stage, non-root, pinned) and its CMD is
  `["python","-m","dsf.cli","--mode","azure","serve-orchestrator"]`.
- `render_runtime_bundle` writes a product-scoped `compose.orchestrator.yml` +
  `.env.orchestrator` under `config/instances/<product>.runtime/`, mapping the Azure
  outputs to the runtime env vars, inlining no secrets.
- `dsf new` renders the bundle on every run; `--execute` (homelab) brings the
  council up via `docker compose up -d` through the injected runner; `aca` raises;
  `deploy_sre` stays deferred.
- The docs no longer claim azure mode is unsupported or the council undeployable.

## Out of scope (named follow-ups)

- **SP3b:** real Azure service adapters (App Configuration, Cosmos, LLM) behind the
  seam. App Configuration recommended first.
- **refactor-cli-runtime-split:** separate the provisioning CLI (`dsf new`,
  `src/dsf/instance/`) from the feature-council runtime source — its own sub-project.
- **SP4+:** squad handoff hardening, SRE agent (`deploy_sre`), brownfield onboarding,
  lifecycle (status/upgrade/destroy), real container image build/publish + `aca`
  bring-up.
