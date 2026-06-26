# Unified `dsf` CLI + Owner App Configuration Runtime-Config Index Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an operator run the feature-council runtime against a provisioned product with just `dsf sweep --product <p>` (no wall of exported Azure endpoints), and fold the `dsfctl` runtime verbs into the single `dsf` binary.

**Architecture:** `dsf new` publishes each product's full runtime env (endpoints + non-secret pointers) into an **owner-level Azure App Configuration index**, keyed by App Config *label = product*. The runtime self-resolves that env from `--product` plus one pointer, `DSF_OWNER_APPCONFIG_ENDPOINT`. The shared resolver lives in `core` (so both members can use it without violating import boundaries); `dsf` exposes `run/sweep/serve-orchestrator/serve-agent` by **subprocessing** `python -m dsf.runtime.control` (the cli member must not import the runtime). Deployed ACA containers are untouched — they still get env injected by Bicep and never read the index.

**Tech Stack:** Python 3.12, `uv` workspace, `argparse`, `azure-appconfiguration` (behind the `ConfigGateway` seam), pytest (`--import-mode=importlib`, `asyncio_mode=auto`), ruff (line-length 100, `E,F,I,UP,B`), import-linter (`uv run lint-imports`, must stay **4 kept / 0 broken**).

**Design spec:** `docs/superpowers/specs/2026-06-26-unify-dsf-cli-owner-appconfig-runtime-config-design.md`

**Critical constraints:**
- **Import boundaries:** `dsf.cli`/`dsf.instance` (cli member) must NOT import `dsf.runtime`/`dsf.orchestrator`/`dsf.triggers`/`dsf.agents`/`dsf.council`/`dsf.evals` (feature-council), and vice versa. grimp detects imports anywhere (including inside functions), so lazy imports do NOT escape the rule. The shared resolver MUST live in `core`; the front-door verbs MUST subprocess, not import.
- **Real-only `src/` (ADR 0014):** no `Fake*`/stub/offline-fallback code in `src/`. Deterministic doubles live in `testing/dsf_testing/`.
- **Index holds NO secret values** — only endpoints and the *names* of secrets (e.g. the private-key secret name), never the PEM itself.
- **Index values are plain strings** (raw env values), NOT JSON-encoded (unlike `AppConfigStore` flags, which `json.dumps` bools).

**Validation commands (run from repo root):**
- `uv run pytest -q`
- `uv run ruff check .`
- `uv run lint-imports`  (expect `Contracts: 4 kept, 0 broken`)

Run each member's targeted tests with the full path, e.g. `uv run pytest core/tests/config/test_owner_index.py -q`.

---

## File Structure

**Created:**
- `core/src/dsf/config/owner_index.py` — publish / read / delete / resolve the per-product runtime-env index. Pure functions over the `ConfigGateway` seam.
- `core/tests/config/test_owner_index.py` — owner_index unit tests (offline, in-memory gateway).
- `cli/src/dsf/instance/runtime_index.py` — assemble the index payload (`dict[str,str]`) for one product from its manifest. Single source of truth for *what* goes into the index.
- `cli/tests/instance/test_runtime_index.py` — payload-assembler tests.
- `cli/tests/instance/test_owner_appconfig_bootstrap.py` — owner App Config bootstrap command-builder + flow tests.
- `cli/tests/cli/test_factory_runtime.py` — front-door run/sweep/serve verb tests.
- `feature-council/tests/runtime/test_control_product.py` — `--product` resolution tests for the runtime CLI.

**Modified:**
- `core/src/dsf/config/azure_store.py` — add `delete(key, label)` to the `ConfigGateway` Protocol and `_SdkConfigGateway`.
- `testing/dsf_testing/azure_doubles.py` — add `delete` to `InMemoryConfigGateway`.
- `feature-council/src/dsf/runtime/control.py` — `--product` on `run`/`sweep`/`serve-orchestrator`; resolve env via `runtime_env_for_product`.
- `cli/src/dsf/cli/factory.py` — front-door `run`/`sweep`/`serve-orchestrator`/`serve-agent` verbs (subprocess); thread `--owner-appconfig-endpoint`; `bootstrap --appconfig-name`; print the new pointer.
- `cli/src/dsf/instance/provisioner.py` — `InstanceProvisioner` gains a `publish_runtime_index` step; `InstanceOffboarder` gains a `remove_runtime_index` step.
- `cli/src/dsf/instance/deprovisioner.py` — `InstanceDeprovisioner` gains a `remove_runtime_index` step.
- `cli/src/dsf/instance/app_bootstrap.py` — owner App Config create + RBAC command builder; `BootstrapConfig`/`BootstrapResult` fields; `bootstrap_app` flow.
- `feature-council/pyproject.toml` — drop the `dsfctl` console script.
- `feature-council/src/dsf/runtime/Dockerfile` — `CMD` → `python -m dsf.runtime.control ...`.
- `feature-council/tests/runtime/test_runtime_image.py` — assert the new `CMD`.
- `cli/tests/cli/test_factory.py` — invert the "factory must not expose runtime ops" assertion.
- `docs/site/get-started/operate.md`, `.env.example`, factory help strings — doc the new flow.

---

## Task 1: Owner App Configuration runtime-config index (core)

Adds the shared resolver and the `delete` seam it needs. This is the foundation every other task builds on.

**Files:**
- Create: `core/src/dsf/config/owner_index.py`
- Create: `core/tests/config/test_owner_index.py`
- Modify: `core/src/dsf/config/azure_store.py:17-22` (Protocol) and `:123-125` (after `_SdkConfigGateway.list`)
- Modify: `testing/dsf_testing/azure_doubles.py:25-26` (after `InMemoryConfigGateway.list`)

- [ ] **Step 1: Write the failing tests**

Create `core/tests/config/test_owner_index.py`:

```python
"""Unit tests for the owner-level runtime-config index (offline gateway)."""

from __future__ import annotations

from dsf.config.owner_index import (
    OWNER_APPCONFIG_ENV,
    delete_runtime_config,
    publish_runtime_config,
    read_runtime_config,
    runtime_env_for_product,
)
from dsf_testing.azure_doubles import InMemoryConfigGateway

ENDPOINT = "https://owner-index.azconfig.io"


def test_publish_then_read_round_trips_only_that_product():
    gw = InMemoryConfigGateway()
    publish_runtime_config(ENDPOINT, "pets", {"A": "1", "B": "2"}, gateway=gw)
    publish_runtime_config(ENDPOINT, "cars", {"A": "9"}, gateway=gw)

    assert read_runtime_config(ENDPOINT, "pets", gateway=gw) == {"A": "1", "B": "2"}
    assert read_runtime_config(ENDPOINT, "cars", gateway=gw) == {"A": "9"}


def test_delete_removes_only_that_products_entries():
    gw = InMemoryConfigGateway()
    publish_runtime_config(ENDPOINT, "pets", {"A": "1", "B": "2"}, gateway=gw)
    publish_runtime_config(ENDPOINT, "cars", {"A": "9"}, gateway=gw)

    delete_runtime_config(ENDPOINT, "pets", gateway=gw)

    assert read_runtime_config(ENDPOINT, "pets", gateway=gw) == {}
    assert read_runtime_config(ENDPOINT, "cars", gateway=gw) == {"A": "9"}


def test_runtime_env_layers_index_under_os_env_and_forces_product():
    gw = InMemoryConfigGateway()
    publish_runtime_config(
        ENDPOINT,
        "pets",
        {"AZURE_OPENAI_ENDPOINT": "from-index", "DSF_PRODUCT": "WRONG"},
        gateway=gw,
    )
    base_env = {"AZURE_OPENAI_ENDPOINT": "from-os", "EXTRA": "kept"}

    env = runtime_env_for_product(
        "pets", owner_endpoint=ENDPOINT, base_env=base_env, gateway=gw
    )

    # os.environ wins over the index; DSF_PRODUCT is forced to the argument.
    assert env["AZURE_OPENAI_ENDPOINT"] == "from-os"
    assert env["EXTRA"] == "kept"
    assert env["DSF_PRODUCT"] == "pets"


def test_runtime_env_without_endpoint_is_base_env_plus_product():
    env = runtime_env_for_product("pets", owner_endpoint="", base_env={"X": "y"})
    assert env == {"X": "y", "DSF_PRODUCT": "pets"}


def test_owner_appconfig_env_name_is_stable():
    assert OWNER_APPCONFIG_ENV == "DSF_OWNER_APPCONFIG_ENDPOINT"
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest core/tests/config/test_owner_index.py -q`
Expected: FAIL with `ModuleNotFoundError: No module named 'dsf.config.owner_index'`.

- [ ] **Step 3: Add `delete` to the `ConfigGateway` seam**

In `core/src/dsf/config/azure_store.py`, add `delete` to the Protocol (after `list` at line 22):

```python
class ConfigGateway(Protocol):
    """Narrow seam over App Configuration: string values keyed by (key, label)."""

    def get(self, key: str, label: str | None) -> str | None: ...
    def set(self, key: str, value: str, label: str | None) -> None: ...
    def list(self) -> list[tuple[str, str, str | None]]: ...
    def delete(self, key: str, label: str | None) -> None: ...
```

And to `_SdkConfigGateway` (after `list` at line 125):

```python
    def delete(self, key: str, label: str | None) -> None:  # pragma: no cover
        client = self._client_or_build()
        client.delete_configuration_setting(key=key, label=label)
```

In `testing/dsf_testing/azure_doubles.py`, add `delete` to `InMemoryConfigGateway` (after `list` at line 26):

```python
    def delete(self, key: str, label: str | None) -> None:
        self._d.pop((key, label), None)
```

- [ ] **Step 4: Write the `owner_index` module**

Create `core/src/dsf/config/owner_index.py`:

```python
"""Owner-level App Configuration index of per-product runtime env.

``dsf new`` publishes a product's runtime env here under App Configuration
*label = <product>*; the runtime resolves it from just ``--product`` plus the
``DSF_OWNER_APPCONFIG_ENDPOINT`` pointer. Values are plain strings (raw env
values), NOT JSON-encoded flags. Secrets never live here -- only endpoints and
non-secret pointers (e.g. the private-key *secret name*, never the PEM itself).
"""

from __future__ import annotations

import os
from collections.abc import Mapping

from dsf.config.azure_store import ConfigGateway, _SdkConfigGateway

OWNER_APPCONFIG_ENV = "DSF_OWNER_APPCONFIG_ENDPOINT"


def _gateway(endpoint: str, gateway: ConfigGateway | None) -> ConfigGateway:
    """Use the injected gateway (tests) or build the real SDK one (production)."""
    return gateway if gateway is not None else _SdkConfigGateway(endpoint)


def publish_runtime_config(
    endpoint: str,
    product: str,
    values: Mapping[str, str],
    *,
    gateway: ConfigGateway | None = None,
) -> None:
    """Write every ``values`` entry to the index under label = ``product``."""
    gw = _gateway(endpoint, gateway)
    for key, value in values.items():
        gw.set(key, value, product)


def read_runtime_config(
    endpoint: str,
    product: str,
    *,
    gateway: ConfigGateway | None = None,
) -> dict[str, str]:
    """Return the index entries stored under label = ``product``."""
    gw = _gateway(endpoint, gateway)
    return {key: value for key, value, label in gw.list() if label == product}


def delete_runtime_config(
    endpoint: str,
    product: str,
    *,
    gateway: ConfigGateway | None = None,
) -> None:
    """Remove every index entry stored under label = ``product``."""
    gw = _gateway(endpoint, gateway)
    for key, _value, label in list(gw.list()):
        if label == product:
            gw.delete(key, product)


def runtime_env_for_product(
    product: str,
    *,
    owner_endpoint: str | None = None,
    base_env: Mapping[str, str] | None = None,
    gateway: ConfigGateway | None = None,
) -> dict[str, str]:
    """Resolve the full runtime env for ``product``.

    Precedence (low to high): index entries < ``base_env`` (os.environ) <
    forced ``DSF_PRODUCT = product``. When no owner endpoint is configured the
    index layer is empty, so the result is just ``base_env`` plus the product.
    """
    env = dict(base_env if base_env is not None else os.environ)
    endpoint = owner_endpoint if owner_endpoint is not None else env.get(OWNER_APPCONFIG_ENV, "")
    index: dict[str, str] = {}
    if endpoint:
        index = read_runtime_config(endpoint, product, gateway=gateway)
    return {**index, **env, "DSF_PRODUCT": product}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `uv run pytest core/tests/config/test_owner_index.py -q`
Expected: PASS (5 passed).

- [ ] **Step 6: Verify import boundaries still hold**

Run: `uv run lint-imports`
Expected: `Contracts: 4 kept, 0 broken`.

- [ ] **Step 7: Commit**

```bash
git add core/src/dsf/config/owner_index.py core/tests/config/test_owner_index.py \
        core/src/dsf/config/azure_store.py testing/dsf_testing/azure_doubles.py
git commit -m "feat: add owner App Config runtime-config index resolver"
```

---

## Task 2: `--product` resolution in the runtime CLI (feature-council)

`dsfctl`'s `run`/`sweep`/`serve-orchestrator` learn a `--product` flag that resolves the full env from the owner index before building services.

**Files:**
- Modify: `feature-council/src/dsf/runtime/control.py:91-128` (`_get_services`, `_cmd_run`, `_cmd_sweep`), `:190-206` (`_cmd_serve_orchestrator`), `:232-256` (parser)
- Test: `feature-council/tests/runtime/test_control_product.py`

- [ ] **Step 1: Write the failing test**

Create `feature-council/tests/runtime/test_control_product.py`:

```python
"""`--product` makes the runtime CLI resolve env from the owner index."""

from __future__ import annotations

import dsf.runtime.control as control


def test_get_services_passes_resolved_env_when_product_given(monkeypatch):
    captured: dict[str, object] = {}

    def fake_build_services(*, env=None):
        captured["env"] = env
        return "services"

    monkeypatch.setattr(control, "build_services", fake_build_services)
    monkeypatch.setattr(
        control,
        "runtime_env_for_product",
        lambda product: {"DSF_PRODUCT": product, "AZURE_OPENAI_ENDPOINT": "x"},
    )

    args = control.build_parser().parse_args(["sweep", "--product", "pets"])
    services = control._get_services(args)

    assert services == "services"
    assert captured["env"] == {"DSF_PRODUCT": "pets", "AZURE_OPENAI_ENDPOINT": "x"}


def test_get_services_uses_plain_env_without_product(monkeypatch):
    captured: dict[str, object] = {"called": False}

    def fake_build_services(*, env=None):
        captured["called"] = True
        captured["env"] = env
        return "services"

    monkeypatch.setattr(control, "build_services", fake_build_services)

    args = control.build_parser().parse_args(["sweep"])
    control._get_services(args)

    assert captured["called"] is True
    assert captured["env"] is None
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest feature-council/tests/runtime/test_control_product.py -q`
Expected: FAIL — `_get_services()` takes no argument / `runtime_env_for_product` not imported.

- [ ] **Step 3: Implement `--product` resolution**

In `feature-council/src/dsf/runtime/control.py`, add the import near the other `dsf.*` imports at the top of the file:

```python
from dsf.config.owner_index import runtime_env_for_product
```

Replace `_get_services` (lines 91-97):

```python
def _get_services(args: argparse.Namespace | None = None):
    """Build the real services bundle or exit cleanly on misconfiguration.

    With ``--product`` the env is resolved from the owner App Config index
    (endpoints + non-secret pointers) before wiring the real Azure adapters.
    """
    product = getattr(args, "product", None) if args is not None else None
    try:
        if product:
            return build_services(env=runtime_env_for_product(product))
        return build_services()
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        sys.exit(1)
```

Update the three call sites to pass `args`:
- `_cmd_run` line 104: `services = _get_services(args)`
- `_cmd_sweep` line 125: `services = _get_services(args)`
- `_cmd_serve_orchestrator` line 198: `services = _get_services(args)`

Add the `--product` argument to each subparser in `build_parser` (after each `set_defaults` is fine; place before it for readability):

```python
    p_run = sub.add_parser("run", help="run the intake line for one signal")
    p_run.add_argument("--dry-run", action="store_true", help="run line, skip filing")
    p_run.add_argument("--signal", help="path to a signal JSON file")
    p_run.add_argument(
        "--product", help="resolve runtime env for this product from the owner index"
    )
    p_run.set_defaults(func=_cmd_run)

    p_sweep = sub.add_parser("sweep", help="run a scheduled sweep")
    p_sweep.add_argument(
        "--product", help="resolve runtime env for this product from the owner index"
    )
    p_sweep.set_defaults(func=_cmd_sweep)
```

And on the `serve-orchestrator` parser (`p_orch`), add before `set_defaults`:

```python
    p_orch.add_argument(
        "--product", help="resolve runtime env for this product from the owner index"
    )
    p_orch.set_defaults(func=_cmd_serve_orchestrator)
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `uv run pytest feature-council/tests/runtime/test_control_product.py -q`
Expected: PASS (2 passed).

- [ ] **Step 5: Verify import boundaries**

Run: `uv run lint-imports`
Expected: `Contracts: 4 kept, 0 broken`.

- [ ] **Step 6: Commit**

```bash
git add feature-council/src/dsf/runtime/control.py \
        feature-council/tests/runtime/test_control_product.py
git commit -m "feat: resolve runtime env from owner index via --product in dsfctl"
```

---

## Task 3: Front-door `run`/`sweep`/`serve-orchestrator`/`serve-agent` verbs on `dsf`

The cli member cannot import the runtime, so the verbs subprocess `python -m dsf.runtime.control` and pass through its exit code.

**Files:**
- Modify: `cli/src/dsf/cli/factory.py:9-12` (imports), add handlers + `_forward_to_runtime`, register subparsers in `build_parser` (before line 535 `add_charter_subcommands`)
- Test: `cli/tests/cli/test_factory_runtime.py`
- Modify: `cli/tests/cli/test_factory.py` (invert the "no runtime ops" assertion)

- [ ] **Step 1: Write the failing test**

Create `cli/tests/cli/test_factory_runtime.py`:

```python
"""The `dsf` front door forwards runtime verbs to `python -m dsf.runtime.control`."""

from __future__ import annotations

import sys
from types import SimpleNamespace

import dsf.cli.factory as factory


def _capture_forward(monkeypatch):
    calls: list[list[str]] = []

    def fake_run(argv, *a, **k):
        calls.append(argv)
        return SimpleNamespace(returncode=0)

    monkeypatch.setattr(factory.subprocess, "run", fake_run)
    return calls


def test_sweep_forwards_product(monkeypatch):
    calls = _capture_forward(monkeypatch)
    rc = factory.main(["sweep", "--product", "pets"])
    assert rc == 0
    assert calls == [
        [sys.executable, "-m", "dsf.runtime.control", "sweep", "--product", "pets"]
    ]


def test_run_forwards_signal_and_dry_run(monkeypatch):
    calls = _capture_forward(monkeypatch)
    rc = factory.main(["run", "--signal", "s.json", "--dry-run", "--product", "pets"])
    assert rc == 0
    assert calls == [
        [
            sys.executable, "-m", "dsf.runtime.control", "run",
            "--signal", "s.json", "--dry-run", "--product", "pets",
        ]
    ]


def test_serve_orchestrator_forwards_loop_and_interval(monkeypatch):
    calls = _capture_forward(monkeypatch)
    rc = factory.main(["serve-orchestrator", "--loop", "--interval", "60"])
    assert rc == 0
    assert calls == [
        [
            sys.executable, "-m", "dsf.runtime.control", "serve-orchestrator",
            "--loop", "--interval", "60",
        ]
    ]


def test_forward_passes_through_nonzero_exit_code(monkeypatch):
    monkeypatch.setattr(
        factory.subprocess, "run", lambda *a, **k: SimpleNamespace(returncode=3)
    )
    assert factory.main(["sweep"]) == 3
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest cli/tests/cli/test_factory_runtime.py -q`
Expected: FAIL — `factory` has no `subprocess` attribute / `sweep` is not a known subcommand.

- [ ] **Step 3: Implement the front door**

In `cli/src/dsf/cli/factory.py`, add `import subprocess` to the module-top imports (next to `import argparse`, `import sys`):

```python
import argparse
import subprocess
import sys
from pathlib import Path
```

Add the forwarder and handlers (place them near the other `_cmd_*` functions, e.g. after `_cmd_delete`):

```python
_RUNTIME_MODULE = "dsf.runtime.control"


def _forward_to_runtime(forward: list[str]) -> int:
    """Run a feature-council runtime verb in a subprocess and pass through its code.

    The cli member must not import the runtime (import-linter forbids it), so the
    front-door run/sweep/serve verbs shell out to
    ``python -m dsf.runtime.control <forward...>``.
    """
    completed = subprocess.run([sys.executable, "-m", _RUNTIME_MODULE, *forward])
    return completed.returncode


def _cmd_run(args: argparse.Namespace) -> int:
    forward = ["run"]
    if args.signal:
        forward += ["--signal", args.signal]
    if args.dry_run:
        forward.append("--dry-run")
    if args.product:
        forward += ["--product", args.product]
    return _forward_to_runtime(forward)


def _cmd_sweep(args: argparse.Namespace) -> int:
    forward = ["sweep"]
    if args.product:
        forward += ["--product", args.product]
    return _forward_to_runtime(forward)


def _cmd_serve_orchestrator(args: argparse.Namespace) -> int:
    forward = ["serve-orchestrator"]
    if args.loop:
        forward.append("--loop")
    if args.interval is not None:
        forward += ["--interval", str(args.interval)]
    if args.product:
        forward += ["--product", args.product]
    return _forward_to_runtime(forward)


def _cmd_serve_agent(args: argparse.Namespace) -> int:
    forward = [
        "serve-agent", "--kind", args.kind, "--host", args.host, "--port", str(args.port)
    ]
    return _forward_to_runtime(forward)
```

Register the subparsers in `build_parser`, immediately before `from dsf.cli.charter import add_charter_subcommands` (line 535):

```python
    p_run = sub.add_parser("run", help="run the intake line for one signal (runtime)")
    p_run.add_argument("--signal", help="path to a signal JSON file")
    p_run.add_argument("--dry-run", action="store_true", help="run the line but skip filing")
    p_run.add_argument("--product", help="resolve runtime env for this product")
    p_run.set_defaults(func=_cmd_run)

    p_sweep = sub.add_parser("sweep", help="sweep enabled source agents once (runtime)")
    p_sweep.add_argument("--product", help="resolve runtime env for this product")
    p_sweep.set_defaults(func=_cmd_sweep)

    p_orch = sub.add_parser(
        "serve-orchestrator", help="run the orchestrator worker (runtime)"
    )
    p_orch.add_argument("--loop", action="store_true", help="sweep continuously")
    p_orch.add_argument("--interval", type=int, default=None, help="seconds between sweeps")
    p_orch.add_argument("--product", help="resolve runtime env for this product")
    p_orch.set_defaults(func=_cmd_serve_orchestrator)

    p_serve = sub.add_parser("serve-agent", help="serve a source agent over A2A (runtime)")
    p_serve.add_argument("--kind", default="sentry", help="source agent kind")
    p_serve.add_argument("--host", default="0.0.0.0", help="bind host")
    p_serve.add_argument("--port", type=int, default=8080, help="bind port")
    p_serve.set_defaults(func=_cmd_serve_agent)
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `uv run pytest cli/tests/cli/test_factory_runtime.py -q`
Expected: PASS (4 passed).

- [ ] **Step 5: Invert the stale assertion in `test_factory.py`**

Find the assertion in `cli/tests/cli/test_factory.py` that asserts the factory does NOT expose runtime ops (around line 190 — search for `sweep` / `serve-orchestrator`). Replace the negative assertion with a positive one. For example, if it reads:

```python
    # factory must NOT expose runtime ops (those live in dsfctl)
    with pytest.raises(SystemExit):
        factory.build_parser().parse_args(["sweep"])
```

replace it with:

```python
    # factory now fronts the runtime ops (subprocessing python -m dsf.runtime.control)
    args = factory.build_parser().parse_args(["sweep", "--product", "pets"])
    assert args.func is factory._cmd_sweep
    assert args.product == "pets"
```

(If the existing test uses a different structure, mirror its style — the key change is asserting `sweep`/`serve-orchestrator`/`serve-agent`/`run` now parse successfully.)

- [ ] **Step 6: Run the factory suite + import boundaries**

Run: `uv run pytest cli/tests/cli/ -q && uv run lint-imports`
Expected: PASS; `Contracts: 4 kept, 0 broken` (no new cli→runtime import — it is a subprocess).

- [ ] **Step 7: Commit**

```bash
git add cli/src/dsf/cli/factory.py cli/tests/cli/test_factory_runtime.py \
        cli/tests/cli/test_factory.py
git commit -m "feat: front runtime run/sweep/serve verbs on the dsf CLI via subprocess"
```

---

## Task 4: Index payload assembler (cli)

One function decides exactly *what* goes into the index for a product, reusing `runtime_endpoint_env` so endpoints never drift from `.env.orchestrator`.

**Files:**
- Create: `cli/src/dsf/instance/runtime_index.py`
- Test: `cli/tests/instance/test_runtime_index.py`

- [ ] **Step 1: Write the failing test**

Create `cli/tests/instance/test_runtime_index.py`:

```python
"""The index payload carries endpoints + non-secret pointers, never secrets."""

from __future__ import annotations

from dsf.instance.runtime_index import runtime_index_values
from dsf.instance.spec import (
    AzureProvisionResult,
    GitHubAppBinding,
    InstanceManifest,
    InstanceSpec,
)


def _manifest() -> InstanceManifest:
    spec = InstanceSpec(product="pets", owner="acme", repo="pets")
    azure = AzureProvisionResult(
        outputs={
            "appConfigEndpoint": "https://pets.azconfig.io",
            "keyVaultUri": "https://kv-pets.vault.azure.net/",
            "appInsightsConnectionString": "InstrumentationKey=abc",
            "cosmosEndpoint": "https://pets.documents.azure.com:443/",
            "openAiEndpoint": "https://pets.openai.azure.com/",
            "openAiDeployment": "gpt-4o",
            "openAiEmbeddingDeployment": "text-embedding-3-large",
        }
    )
    app = GitHubAppBinding(
        app_id="123", installation_id="456", private_key_secret="dsf-app-private-key"
    )
    return InstanceManifest(spec=spec, azure=azure, github_app=app)


def test_payload_carries_endpoints_pointers_and_product():
    values = runtime_index_values(_manifest())

    assert values["AZURE_APPCONFIG_ENDPOINT"] == "https://pets.azconfig.io"
    assert values["AZURE_OPENAI_DEPLOYMENT"] == "gpt-4o"
    assert values["DSF_PRODUCT"] == "pets"
    assert values["GITHUB_REPOSITORY"] == "acme/pets"
    assert values["GITHUB_APP_ID"] == "123"
    assert values["GITHUB_INSTALLATION_ID"] == "456"
    assert values["GITHUB_APP_PRIVATE_KEY_SECRET"] == "dsf-app-private-key"
    assert values["WEBIQ_PROVIDER"] == "webiq"
    assert values["WEBIQ_API_KEY_SECRET"] == "webiq-api-key"


def test_payload_never_contains_secret_values():
    values = runtime_index_values(_manifest())
    joined = "\n".join(f"{k}={v}" for k, v in values.items())
    # The private-key *secret name* is allowed; PEM material is not.
    assert "BEGIN" not in joined
    assert "PRIVATE KEY-----" not in joined


def test_payload_omits_app_keys_when_no_binding():
    spec = InstanceSpec(product="pets", owner="acme", repo="pets")
    values = runtime_index_values(InstanceManifest(spec=spec))
    assert "GITHUB_APP_ID" not in values
    assert values["GITHUB_REPOSITORY"] == "acme/pets"
```

> Note: confirm the `InstanceSpec`/`AzureProvisionResult`/`GitHubAppBinding` constructor signatures against `cli/src/dsf/instance/spec.py` before running; adjust the keyword args in the test fixture to match (e.g. `InstanceSpec` may require additional fields with defaults). The output-key names (`appConfigEndpoint`, `keyVaultUri`, ...) must match `_ENDPOINT_MAP` in `cli/src/dsf/instance/runtime_render.py`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_runtime_index.py -q`
Expected: FAIL — `No module named 'dsf.instance.runtime_index'`.

- [ ] **Step 3: Implement the assembler**

Create `cli/src/dsf/instance/runtime_index.py`:

```python
"""Assemble the owner-index payload (one dict) for a single product.

The index carries endpoints (reusing ``runtime_endpoint_env`` so they never
drift from ``.env.orchestrator``), the WebIQ pointers, and the GitHub App
binding *pointers* (app id, installation id, private-key secret *name*). It
never carries secret material -- the runtime reads secrets from the product Key
Vault at ``build_services`` time.
"""

from __future__ import annotations

from dsf.instance.runtime_render import runtime_endpoint_env
from dsf.instance.spec import InstanceManifest

_STATIC = {"WEBIQ_PROVIDER": "webiq", "WEBIQ_API_KEY_SECRET": "webiq-api-key"}


def runtime_index_values(manifest: InstanceManifest) -> dict[str, str]:
    """Return the full ``{env_key: value}`` map to publish for this product."""
    outputs = manifest.azure.outputs if manifest.azure else {}
    values = dict(runtime_endpoint_env(outputs))
    values.update(_STATIC)
    values["DSF_PRODUCT"] = manifest.spec.product
    values["GITHUB_REPOSITORY"] = manifest.spec.github_repo()
    app = manifest.github_app
    if app is not None:
        values["GITHUB_APP_ID"] = app.app_id
        values["GITHUB_INSTALLATION_ID"] = app.installation_id
        values["GITHUB_APP_PRIVATE_KEY_SECRET"] = app.private_key_secret
    return values
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `uv run pytest cli/tests/instance/test_runtime_index.py -q`
Expected: PASS (3 passed).

- [ ] **Step 5: Commit**

```bash
git add cli/src/dsf/instance/runtime_index.py cli/tests/instance/test_runtime_index.py
git commit -m "feat: assemble owner-index payload from the instance manifest"
```

---

## Task 5: `dsf new` publishes the index (provisioner)

A new `publish_runtime_index` provisioning step writes the payload into the owner App Config after outputs + the App binding are known.

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py` — `InstanceProvisioner.__init__` (~line 210-235), the plan step list (insert before the `deploy_council` step, ~line 315), `_execute_step` dispatch (~line 469, before the `deploy_council` branch), and add `_publish_runtime_index`
- Modify: `cli/src/dsf/cli/factory.py` — `_cmd_new` (~line 228-235) threads `owner_appconfig_endpoint`; `p_new` subparser adds `--owner-appconfig-endpoint`
- Test: `cli/tests/instance/test_provisioner.py` (add cases)

- [ ] **Step 1: Write the failing tests**

Add to `cli/tests/instance/test_provisioner.py`:

```python
def test_plan_includes_publish_runtime_index_step():
    from dsf.instance.provisioner import InstanceProvisioner
    from dsf.instance.spec import InstanceSpec

    spec = InstanceSpec(product="pets", owner="acme", repo="pets")
    prov = InstanceProvisioner(
        spec, owner_appconfig_endpoint="https://owner-index.azconfig.io"
    )
    names = [s.name for s in prov.plan().steps]
    assert "publish_runtime_index" in names
    # must run after the App is installed and before the council is deployed
    assert names.index("publish_runtime_index") < names.index("deploy_council")


def test_publish_runtime_index_writes_payload_under_product_label(tmp_path):
    from dsf.instance.provisioner import InstanceProvisioner
    from dsf.instance.spec import (
        AzureProvisionResult,
        GitHubAppBinding,
        InstanceManifest,
        InstanceSpec,
    )
    from dsf_testing.azure_doubles import InMemoryConfigGateway

    gateway = InMemoryConfigGateway()
    spec = InstanceSpec(product="pets", owner="acme", repo="pets")
    prov = InstanceProvisioner(
        spec,
        owner_appconfig_endpoint="https://owner-index.azconfig.io",
        appconfig_gateway=gateway,
    )
    manifest = InstanceManifest(
        spec=spec,
        azure=AzureProvisionResult(outputs={"appConfigEndpoint": "https://pets.azconfig.io"}),
        github_app=GitHubAppBinding(
            app_id="1", installation_id="2", private_key_secret="dsf-app-private-key"
        ),
    )

    prov._publish_runtime_index(manifest)

    stored = {k: v for k, v, label in gateway.list() if label == "pets"}
    assert stored["AZURE_APPCONFIG_ENDPOINT"] == "https://pets.azconfig.io"
    assert stored["DSF_PRODUCT"] == "pets"
    assert stored["GITHUB_APP_ID"] == "1"
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k publish_runtime_index -q`
Expected: FAIL — `InstanceProvisioner.__init__` got an unexpected keyword `owner_appconfig_endpoint`.

- [ ] **Step 3: Implement the provisioner step**

In `cli/src/dsf/instance/provisioner.py`, extend `InstanceProvisioner.__init__` (add the two keyword params with defaults, alongside `github_app_id` etc.):

```python
        owner_appconfig_endpoint: str = "",
        appconfig_gateway: object | None = None,
```

and store them:

```python
        self._owner_appconfig_endpoint = owner_appconfig_endpoint
        self._appconfig_gateway = appconfig_gateway
```

Insert the step into the plan list, immediately before the `deploy_council` `ProvisionStep` (around line 315):

```python
            ProvisionStep(
                name="publish_runtime_index",
                description=(
                    f"Publish {s.product} runtime env (endpoints + pointers) to the "
                    "owner App Configuration index"
                ),
            ),
```

Add the dispatch branch in `_execute_step`, immediately before the `elif step.name == "deploy_council":` branch (line 469):

```python
        elif step.name == "publish_runtime_index":
            if not self._owner_appconfig_endpoint:
                step.result = "skipped (no owner App Config configured)"
            elif not execute:
                step.result = "published (dry-run)"
            else:
                provisional = InstanceManifest(
                    spec=self.spec, plan=plan, executed=executed,
                    azure=azure_result, github_app=self._app_binding,
                )
                self._publish_runtime_index(provisional)
                step.executed, step.result = True, "published"
```

Add the method (place near `_seed_appconfig`, ~line 805):

```python
    def _publish_runtime_index(self, manifest: InstanceManifest) -> None:
        """Publish this product's runtime env into the owner App Config index."""
        from dsf.config.owner_index import publish_runtime_config
        from dsf.instance.runtime_index import runtime_index_values

        publish_runtime_config(
            self._owner_appconfig_endpoint,
            self.spec.product,
            runtime_index_values(manifest),
            gateway=self._appconfig_gateway,
        )
```

- [ ] **Step 4: Thread the endpoint from `dsf new`**

In `cli/src/dsf/cli/factory.py` `_cmd_new`, after `owner_kv = ...` (line 221) add:

```python
    owner_appconfig = args.owner_appconfig_endpoint or os.environ.get(
        "DSF_OWNER_APPCONFIG_ENDPOINT", ""
    )
```

and pass it into the `InstanceProvisioner(...)` call (line 228):

```python
    prov = InstanceProvisioner(
        spec,
        repo_root=root,
        owner_keyvault_uri=owner_kv,
        owner_appconfig_endpoint=owner_appconfig,
        github_app_id=app_id,
        github_installation_id=installation_id,
        admin_principal_id=admin_principal_id,
    )
```

Add the flag to the `p_new` subparser (near `--owner-keyvault-uri` in `build_parser`):

```python
    p_new.add_argument(
        "--owner-appconfig-endpoint",
        default=None,
        help="owner App Configuration endpoint to publish this product's runtime env "
        "into (default: DSF_OWNER_APPCONFIG_ENDPOINT)",
    )
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k publish_runtime_index -q`
Expected: PASS (2 passed).

- [ ] **Step 6: Run the full cli instance suite + import boundaries**

Run: `uv run pytest cli/tests/instance/ -q && uv run lint-imports`
Expected: PASS; `Contracts: 4 kept, 0 broken`.

- [ ] **Step 7: Commit**

```bash
git add cli/src/dsf/instance/provisioner.py cli/src/dsf/cli/factory.py \
        cli/tests/instance/test_provisioner.py
git commit -m "feat: publish product runtime env to owner index during dsf new"
```

---

## Task 6: `dsf offboard` / `dsf delete` remove the index entry

Both teardown classes get a `remove_runtime_index` step. (Two classes: `InstanceOffboarder` in `provisioner.py` backs `dsf offboard`; `InstanceDeprovisioner` in `deprovisioner.py` backs `dsf delete`.)

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py` — `InstanceOffboarder.__init__` (~line 949-962), plan step list (~line 996, before `remove_instance_artifacts`), `_execute_step` (~line 1059)
- Modify: `cli/src/dsf/instance/deprovisioner.py` — `InstanceDeprovisioner.__init__` (~line 84-99), `from_product` (~line 306-330), plan step list (~line 153, before `delete_config`), `_execute_step` (~line 243)
- Modify: `cli/src/dsf/cli/factory.py` — `_cmd_offboard` (~line 296), `_cmd_delete` (~line 354) thread the endpoint; add `--owner-appconfig-endpoint` to `p_offboard` and `p_delete`
- Test: `cli/tests/instance/test_provisioner.py` and `cli/tests/instance/test_deprovisioner.py`

- [ ] **Step 1: Write the failing tests**

Add to `cli/tests/instance/test_provisioner.py`:

```python
def test_offboarder_removes_runtime_index_entry(tmp_path, monkeypatch):
    from dsf.instance.provisioner import InstanceOffboarder
    from dsf.config.owner_index import publish_runtime_config, read_runtime_config
    from dsf_testing.azure_doubles import InMemoryConfigGateway

    gateway = InMemoryConfigGateway()
    publish_runtime_config("https://o.azconfig.io", "pets", {"A": "1"}, gateway=gateway)

    off = InstanceOffboarder(
        "pets",
        owner_appconfig_endpoint="https://o.azconfig.io",
        appconfig_gateway=gateway,
    )
    step = next(s for s in off.plan().steps if s.name == "remove_runtime_index")
    off._execute_step(step, execute=True)

    assert step.result == "removed"
    assert read_runtime_config("https://o.azconfig.io", "pets", gateway=gateway) == {}
```

Add to `cli/tests/instance/test_deprovisioner.py` (build a deprovisioner from an in-memory manifest the way the existing tests do):

```python
def test_deprovisioner_removes_runtime_index_entry():
    from dsf.instance.deprovisioner import InstanceDeprovisioner
    from dsf.instance.spec import InstanceManifest, InstanceSpec
    from dsf.config.owner_index import publish_runtime_config, read_runtime_config
    from dsf_testing.azure_doubles import InMemoryConfigGateway

    gateway = InMemoryConfigGateway()
    publish_runtime_config("https://o.azconfig.io", "pets", {"A": "1"}, gateway=gateway)

    manifest = InstanceManifest(spec=InstanceSpec(product="pets", owner="acme", repo="pets"))
    deprv = InstanceDeprovisioner(
        manifest,
        owner_appconfig_endpoint="https://o.azconfig.io",
        appconfig_gateway=gateway,
    )
    step = next(s for s in deprv.plan().steps if s.name == "remove_runtime_index")
    deprv._execute_step(step)

    assert step.result == "removed"
    assert read_runtime_config("https://o.azconfig.io", "pets", gateway=gateway) == {}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest cli/tests/instance/test_provisioner.py cli/tests/instance/test_deprovisioner.py -k runtime_index -q`
Expected: FAIL — unexpected keyword `owner_appconfig_endpoint`.

- [ ] **Step 3: Implement the offboarder step**

In `cli/src/dsf/instance/provisioner.py`, extend `InstanceOffboarder.__init__` (add params + store):

```python
        owner_appconfig_endpoint: str = "",
        appconfig_gateway: object | None = None,
```
```python
        self._owner_appconfig_endpoint = owner_appconfig_endpoint
        self._appconfig_gateway = appconfig_gateway
```

Insert the step in `InstanceOffboarder.plan()` immediately before the `remove_instance_artifacts` step (~line 1000):

```python
                ProvisionStep(
                    name="remove_runtime_index",
                    description=(
                        f"Remove {self.product} from the owner App Configuration index"
                    ),
                ),
```

Add the dispatch branch in `InstanceOffboarder._execute_step` before `remove_instance_artifacts` (~line 1059):

```python
        elif step.name == "remove_runtime_index":
            if not self._owner_appconfig_endpoint:
                step.result = "skipped (no owner App Config configured)"
            elif not execute:
                step.result = "removed (dry-run)"
            else:
                from dsf.config.owner_index import delete_runtime_config

                delete_runtime_config(
                    self._owner_appconfig_endpoint, self.product,
                    gateway=self._appconfig_gateway,
                )
                step.executed, step.result = True, "removed"
```

- [ ] **Step 4: Implement the deprovisioner step**

In `cli/src/dsf/instance/deprovisioner.py`, extend `InstanceDeprovisioner.__init__` (add params + store after `self._delete_repo`):

```python
        owner_appconfig_endpoint: str = "",
        appconfig_gateway: object | None = None,
```
```python
        self._owner_appconfig_endpoint = owner_appconfig_endpoint
        self._appconfig_gateway = appconfig_gateway
```

Forward them in `from_product` (add the two params to its signature with the same defaults, and pass them into `cls(...)`):

```python
        owner_appconfig_endpoint: str = "",
        appconfig_gateway: object | None = None,
```
```python
        return cls(
            manifest,
            run=run,
            repo_root=repo_root,
            purge=purge,
            delete_repo=delete_repo,
            owner_appconfig_endpoint=owner_appconfig_endpoint,
            appconfig_gateway=appconfig_gateway,
        )
```

Insert the step in `plan()` immediately before the `delete_config` step (~line 159):

```python
            ProvisionStep(
                name="remove_runtime_index",
                description=(
                    f"Remove {s.product} from the owner App Configuration index"
                ),
            ),
```

Add the dispatch branch in `_execute_step` after the `deregister_product` branch (~line 242):

```python
        elif step.name == "remove_runtime_index":
            if not self._owner_appconfig_endpoint:
                step.executed, step.result = True, "skipped (no owner App Config configured)"
            else:
                from dsf.config.owner_index import delete_runtime_config

                delete_runtime_config(
                    self._owner_appconfig_endpoint, self.spec.product,
                    gateway=self._appconfig_gateway,
                )
                step.executed, step.result = True, "removed"
```

- [ ] **Step 5: Thread the endpoint from the CLI**

In `cli/src/dsf/cli/factory.py`, `_cmd_offboard` — resolve and pass the endpoint:

```python
    import os

    owner_appconfig = args.owner_appconfig_endpoint or os.environ.get(
        "DSF_OWNER_APPCONFIG_ENDPOINT", ""
    )
    offboarder = InstanceOffboarder(
        args.product,
        repo_root=root,
        purge=args.purge,
        owner_appconfig_endpoint=owner_appconfig,
    )
```

`_cmd_delete` — same, into `InstanceDeprovisioner.from_product`:

```python
    import os

    owner_appconfig = args.owner_appconfig_endpoint or os.environ.get(
        "DSF_OWNER_APPCONFIG_ENDPOINT", ""
    )
    ...
        deprv = InstanceDeprovisioner.from_product(
            args.product,
            repo_root=root,
            purge=args.purge,
            owner_appconfig_endpoint=owner_appconfig,
        )
```

Add `--owner-appconfig-endpoint` (same help text as Task 5) to both the `p_offboard` and `p_delete` subparsers in `build_parser`:

```python
    p_offboard.add_argument(
        "--owner-appconfig-endpoint",
        default=None,
        help="owner App Configuration endpoint to remove this product's runtime env "
        "from (default: DSF_OWNER_APPCONFIG_ENDPOINT)",
    )
```
```python
    p_delete.add_argument(
        "--owner-appconfig-endpoint",
        default=None,
        help="owner App Configuration endpoint to remove this product's runtime env "
        "from (default: DSF_OWNER_APPCONFIG_ENDPOINT)",
    )
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_provisioner.py cli/tests/instance/test_deprovisioner.py -k runtime_index -q`
Expected: PASS (2 passed).

- [ ] **Step 7: Run the full cli suite + import boundaries**

Run: `uv run pytest cli/tests -q && uv run lint-imports`
Expected: PASS; `Contracts: 4 kept, 0 broken`.

- [ ] **Step 8: Commit**

```bash
git add cli/src/dsf/instance/provisioner.py cli/src/dsf/instance/deprovisioner.py \
        cli/src/dsf/cli/factory.py cli/tests/instance/test_provisioner.py \
        cli/tests/instance/test_deprovisioner.py
git commit -m "feat: remove owner-index entry on dsf offboard and dsf delete"
```

---

## Task 7: `dsf bootstrap` provisions the owner App Configuration

Bootstrap creates the owner App Config (Standard SKU, local auth disabled) in `rg-dsf-app` and grants the operator "App Configuration Data Owner", reusing the RBAC-propagation pattern.

**Files:**
- Modify: `cli/src/dsf/instance/app_bootstrap.py` — add `_APPCONFIG_DATA_OWNER` const + `owner_appconfig_ensure_commands`; extend `BootstrapConfig` (line 180-186) + `BootstrapResult` (line 189-196); extend `bootstrap_app` (line 240-278)
- Test: `cli/tests/instance/test_owner_appconfig_bootstrap.py`

- [ ] **Step 1: Write the failing tests**

Create `cli/tests/instance/test_owner_appconfig_bootstrap.py`:

```python
"""Owner App Configuration provisioning during `dsf bootstrap`."""

from __future__ import annotations

from dsf.instance.app_bootstrap import owner_appconfig_ensure_commands


def test_owner_appconfig_ensure_commands_shape():
    cmds = owner_appconfig_ensure_commands(
        resource_group="rg-dsf-app",
        appconfig_name="dsf-owner-index",
        location="swedencentral",
        operator_object_id="oid-123",
    )

    create = next(c for c in cmds if c[:3] == ["az", "appconfig", "create"])
    assert "--name" in create and "dsf-owner-index" in create
    assert "--sku" in create and "Standard" in create
    assert "--disable-local-auth" in create and "true" in create

    grant = next(c for c in cmds if c[:4] == ["az", "role", "assignment", "create"])
    assert "App Configuration Data Owner" in grant
    assert "oid-123" in grant
    scope = grant[grant.index("--scope") + 1]
    assert scope.endswith(
        "/providers/Microsoft.AppConfiguration/configurationStores/dsf-owner-index"
    )
    assert "{subscription}" in scope  # substituted by bootstrap_app
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_owner_appconfig_bootstrap.py -q`
Expected: FAIL — `cannot import name 'owner_appconfig_ensure_commands'`.

- [ ] **Step 3: Implement the command builder + config/result fields**

In `cli/src/dsf/instance/app_bootstrap.py`, add the role constant near `_SECRETS_OFFICER` (top of file):

```python
_APPCONFIG_DATA_OWNER = "App Configuration Data Owner"
```

Add the builder after `owner_kv_ensure_commands` (line 154):

```python
def owner_appconfig_ensure_commands(
    *, resource_group: str, appconfig_name: str, location: str, operator_object_id: str
) -> list[list[str]]:
    """Build `az` commands creating the owner App Config (RBAC, local auth off)."""
    return [
        [
            "az", "appconfig", "create", "--name", appconfig_name,
            "--resource-group", resource_group, "--location", location,
            "--sku", "Standard", "--disable-local-auth", "true",
        ],
        [
            "az", "role", "assignment", "create",
            "--role", _APPCONFIG_DATA_OWNER,
            "--assignee-object-id", operator_object_id,
            "--assignee-principal-type", "User",
            "--scope",
            f"/subscriptions/{{subscription}}/resourceGroups/{resource_group}"
            f"/providers/Microsoft.AppConfiguration/configurationStores/{appconfig_name}",
        ],
    ]
```

Extend `BootstrapConfig` (line 180) with the App Config name:

```python
@dataclass(frozen=True)
class BootstrapConfig:
    """Operator inputs for a one-time `dsf bootstrap`."""

    app_name: str
    resource_group: str
    keyvault_name: str
    appconfig_name: str
    location: str = "swedencentral"
```

Extend `BootstrapResult` (line 189) with the App Config pointer:

```python
@dataclass(frozen=True)
class BootstrapResult:
    """What the operator needs after bootstrap: the App identity + owner pointers."""

    app_id: str
    installation_id: str
    keyvault_name: str
    keyvault_uri: str
    appconfig_name: str
    appconfig_endpoint: str
```

- [ ] **Step 4: Write the failing flow test**

Add to `cli/tests/instance/test_owner_appconfig_bootstrap.py`:

Add this import to the top of the test file (next to the existing
`owner_appconfig_ensure_commands` import):

```python
from dsf.instance.app_bootstrap import _APPCONFIG_DATA_OWNER
```

Then add the test:

```python
def test_bootstrap_app_creates_owner_appconfig_and_returns_endpoint():
    from dataclasses import dataclass

    from dsf.instance.app_bootstrap import BootstrapConfig, bootstrap_app

    @dataclass
    class _Creds:
        app_id: str = "app-1"
        pem: str = "-----BEGIN-----\nx\n-----END-----"

    calls: list[list[str]] = []

    def fake_run(cmd, *a, **k):
        calls.append(cmd)
        from types import SimpleNamespace

        # `az account show`/`signed-in-user show` are read via stdout.
        if cmd[:3] == ["az", "account", "show"]:
            return SimpleNamespace(stdout="sub-9", returncode=0)
        if cmd[:2] == ["az", "ad"]:
            return SimpleNamespace(stdout="oid-7", returncode=0)
        return SimpleNamespace(stdout="", returncode=0)

    cfg = BootstrapConfig(
        app_name="DSF",
        resource_group="rg-dsf-app",
        keyvault_name="kv-dsf",
        appconfig_name="dsf-owner-index",
    )
    result = bootstrap_app(
        cfg,
        run=fake_run,
        capture_code=lambda manifest: "code",
        exchange=lambda code: _Creds(),
        discover=lambda creds: "install-2",
        write_pem=lambda pem: "/tmp/x.pem",
        sleep=lambda s: None,
    )

    assert result.appconfig_endpoint == "https://dsf-owner-index.azconfig.io"
    assert any(c[:3] == ["az", "appconfig", "create"] for c in calls)
    # subscription placeholder was substituted in the role-assignment scope.
    grant = next(
        c for c in calls
        if c[:4] == ["az", "role", "assignment", "create"]
        and _APPCONFIG_DATA_OWNER in c
    )
    scope = grant[grant.index("--scope") + 1]
    assert "/subscriptions/sub-9/" in scope
```

- [ ] **Step 5: Run the flow test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_owner_appconfig_bootstrap.py -q`
Expected: FAIL — `BootstrapConfig` missing `appconfig_name` / no `appconfig create` call.

- [ ] **Step 6: Wire owner App Config into `bootstrap_app`**

In `cli/src/dsf/instance/app_bootstrap.py` `bootstrap_app`, after the owner-KV ensure loop (line 248) and before `pem_path = write_pem(...)` (line 250), add:

```python
    for cmd in owner_appconfig_ensure_commands(
        resource_group=cfg.resource_group,
        appconfig_name=cfg.appconfig_name,
        location=cfg.location,
        operator_object_id=operator_oid,
    ):
        cmd = [part.replace("{subscription}", subscription) for part in cmd]
        runner(cmd, check=True)
```

Extend the returned `BootstrapResult` (line 273) with the App Config fields:

```python
    return BootstrapResult(
        app_id=creds.app_id,
        installation_id=installation_id,
        keyvault_name=cfg.keyvault_name,
        keyvault_uri=f"https://{cfg.keyvault_name}.vault.azure.net/",
        appconfig_name=cfg.appconfig_name,
        appconfig_endpoint=f"https://{cfg.appconfig_name}.azconfig.io",
    )
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_owner_appconfig_bootstrap.py -q`
Expected: PASS (2 passed).

- [ ] **Step 8: Commit**

```bash
git add cli/src/dsf/instance/app_bootstrap.py \
        cli/tests/instance/test_owner_appconfig_bootstrap.py
git commit -m "feat: provision owner App Configuration during dsf bootstrap"
```

---

## Task 8: `dsf bootstrap` CLI surfaces `--appconfig-name` + the pointer

Wire the new `BootstrapConfig.appconfig_name` through the CLI and print the `export` line operators need.

**Files:**
- Modify: `cli/src/dsf/cli/factory.py` — `_cmd_bootstrap` (line 256-273), `p_boot` subparser (line 491-505)
- Test: `cli/tests/cli/test_factory.py` (or `test_factory_runtime.py`) — bootstrap arg-parsing

- [ ] **Step 1: Write the failing test**

Add to `cli/tests/cli/test_factory.py`:

```python
def test_bootstrap_parser_requires_appconfig_name():
    import dsf.cli.factory as factory

    args = factory.build_parser().parse_args(
        [
            "bootstrap",
            "--app-name", "DSF",
            "--keyvault-name", "kv-dsf",
            "--appconfig-name", "dsf-owner-index",
        ]
    )
    assert args.appconfig_name == "dsf-owner-index"
    assert args.func is factory._cmd_bootstrap
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest cli/tests/cli/test_factory.py -k bootstrap_parser_requires_appconfig_name -q`
Expected: FAIL — `unrecognized arguments: --appconfig-name`.

- [ ] **Step 3: Implement the CLI wiring**

In `cli/src/dsf/cli/factory.py`, extend `_cmd_bootstrap` (line 260):

```python
    cfg = BootstrapConfig(
        app_name=args.app_name,
        resource_group=args.resource_group,
        keyvault_name=args.keyvault_name,
        appconfig_name=args.appconfig_name,
        location=args.location,
    )
    result = bootstrap_app(cfg, capture_code=_browser_capture_code)
    print(
        f"[dsf] DSF GitHub App created: app_id={result.app_id} "
        f"installation_id={result.installation_id}"
    )
    print(f"[dsf] master credentials stored in owner Key Vault {result.keyvault_name}")
    print(f"[dsf] owner App Configuration index ready: {result.appconfig_name}")
    print(f"[dsf] now export DSF_OWNER_KEYVAULT_URI={result.keyvault_uri} for `dsf new`")
    print(
        f"[dsf] and  export DSF_OWNER_APPCONFIG_ENDPOINT={result.appconfig_endpoint} "
        "for `dsf new` / `dsf sweep --product`"
    )
    return 0
```

Add the `--appconfig-name` argument to `p_boot` (after `--keyvault-name`, line 498):

```python
    p_boot.add_argument(
        "--appconfig-name",
        required=True,
        help="owner App Configuration store name for the runtime-config index",
    )
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `uv run pytest cli/tests/cli/test_factory.py -k bootstrap_parser_requires_appconfig_name -q`
Expected: PASS.

- [ ] **Step 5: Run the full cli suite**

Run: `uv run pytest cli/tests -q`
Expected: PASS (existing bootstrap tests that construct `BootstrapConfig`/call `bootstrap_app` may need the new `appconfig_name` kwarg — update any that fail with a missing-argument `TypeError`).

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/cli/factory.py cli/tests/cli/test_factory.py
git commit -m "feat: add dsf bootstrap --appconfig-name and print the index pointer"
```

---

## Task 9: Retire the `dsfctl` console script + fix the runtime image

The runtime stays runnable as `python -m dsf.runtime.control`; only the standalone `dsfctl` entry point goes away. The deployed image CMD switches to module form.

**Files:**
- Modify: `feature-council/pyproject.toml:15` (remove the `dsfctl` script)
- Modify: `feature-council/src/dsf/runtime/Dockerfile:21,26`
- Modify: `feature-council/tests/runtime/test_runtime_image.py:36`

- [ ] **Step 1: Update the failing image assertion first**

In `feature-council/tests/runtime/test_runtime_image.py`, change line 36:

```python
    assert (
        'CMD ["python", "-m", "dsf.runtime.control", "serve-orchestrator", "--loop"]'
        in text
    )
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest feature-council/tests/runtime/test_runtime_image.py -q`
Expected: FAIL — the Dockerfile still has the old `dsfctl` CMD.

- [ ] **Step 3: Update the Dockerfile**

In `feature-council/src/dsf/runtime/Dockerfile`, line 21 comment + line 26 CMD:

```dockerfile
# Per-product feature-council orchestrator (the runtime instance-control module).
```
```dockerfile
CMD ["python", "-m", "dsf.runtime.control", "serve-orchestrator", "--loop"]
```

- [ ] **Step 4: Remove the `dsfctl` console script**

In `feature-council/pyproject.toml`, delete line 15:

```toml
dsfctl = "dsf.runtime.control:main"
```

(Leave the `[project.scripts]` table header if other scripts remain; if `dsfctl` was the only entry, remove the now-empty table.)

- [ ] **Step 5: Run the image test + re-sync the workspace**

Run: `uv run pytest feature-council/tests/runtime/test_runtime_image.py -q`
Expected: PASS.

Run: `uv sync --all-packages`
Expected: succeeds; `dsfctl` is no longer installed (`dsf` remains).

- [ ] **Step 6: Commit**

```bash
git add feature-council/pyproject.toml feature-council/src/dsf/runtime/Dockerfile \
        feature-council/tests/runtime/test_runtime_image.py
git commit -m "build: drop dsfctl console script; run the runtime as a module"
```

---

## Task 10: Docs + `.env.example`

Document the new operator flow so the runbook matches reality.

**Files:**
- Modify: `docs/site/get-started/operate.md`
- Modify: `.env.example` (repo root — add the pointer)
- Modify: `cli/src/dsf/cli/factory.py` module docstring (lines 1-7) — mention the runtime verbs

- [ ] **Step 1: Update `.env.example`**

Add (near the other `DSF_OWNER_*` / Azure endpoint vars):

```bash
# Pointer to the owner-level App Configuration runtime-config index. With this set,
# `dsf sweep --product <p>` (and run/serve-orchestrator) self-resolve every Azure
# endpoint for <p> from the index instead of requiring them all in the environment.
DSF_OWNER_APPCONFIG_ENDPOINT=https://dsf-owner-index.azconfig.io
```

- [ ] **Step 2: Update `operate.md`**

Replace the `dsfctl sweep` operating instructions with the unified flow. Add a section like:

```markdown
## Operating a product runtime

One-time, per owner (during `dsf bootstrap`): the owner App Configuration index is
created and you export its pointer:

```bash
export DSF_OWNER_APPCONFIG_ENDPOINT=https://dsf-owner-index.azconfig.io
```

Then operate any provisioned product by name — no per-product endpoint exports:

```bash
dsf sweep --product pets-corp-2              # one sweep across enabled sources
dsf run --product pets-corp-2 --signal s.json --dry-run
dsf serve-orchestrator --product pets-corp-2 # single tick (omit for --loop)
```

`dsf new --product pets-corp-2` publishes that product's runtime env (endpoints +
non-secret pointers, never secrets) into the index; `dsf offboard` / `dsf delete`
remove it. The deployed Azure Container App still gets its env from Bicep and never
reads the index — the `--product` path is for local/CI operation.
```

(Search `operate.md` for any remaining `dsfctl` mentions and replace them with the `dsf` equivalents.)

- [ ] **Step 3: Update the factory module docstring**

In `cli/src/dsf/cli/factory.py`, extend the module docstring (after the `dsf offboard` sentence):

```python
``dsf`` also fronts the feature-council runtime: ``dsf sweep``/``run``/
``serve-orchestrator``/``serve-agent`` forward to ``python -m dsf.runtime.control``,
resolving per-product env from the owner App Configuration index via ``--product``.
```

- [ ] **Step 4: Grep for stragglers**

Run: `git grep -n "dsfctl" -- ':!docs/superpowers' ':!docs/adr'`
Expected: only intentional historical references remain (e.g. ADRs). Replace any operator-facing `dsfctl <verb>` usage in `docs/site/` with `dsf <verb>`.

- [ ] **Step 5: Commit**

```bash
git add docs/site/get-started/operate.md .env.example cli/src/dsf/cli/factory.py
git commit -m "docs: document the unified dsf runtime verbs + owner App Config index"
```

---

## Task 11: Full gate run

**Files:** none (verification only).

- [ ] **Step 1: ruff**

Run: `uv run ruff check .`
Expected: `All checks passed!`

- [ ] **Step 2: import boundaries**

Run: `uv run lint-imports`
Expected: `Contracts: 4 kept, 0 broken`.

- [ ] **Step 3: full test suite**

Run: `uv run pytest -q`
Expected: all green (one pre-existing Starlette deprecation warning is acceptable).

- [ ] **Step 4: smoke the unified CLI**

Run: `uv run dsf --help` and `uv run dsf sweep --help`
Expected: `sweep`, `run`, `serve-orchestrator`, `serve-agent`, `new`, `offboard`, `delete`, `bootstrap` all listed; `sweep --help` shows `--product`.

- [ ] **Step 5: Final commit (if any fixups were needed)**

```bash
git add -A
git commit -m "chore: finalize unified dsf CLI + owner App Config runtime index"
```
