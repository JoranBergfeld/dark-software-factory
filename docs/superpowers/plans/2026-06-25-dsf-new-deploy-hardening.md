# `dsf new` Deploy Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `dsf new`'s Azure deploy resilient — let operators opt out of Bing grounding, and bound the deploy poller so a wedged resource can't hang the CLI forever.

**Architecture:** Three small, independent changes plus a docs note. (1) A new `InstanceSpec.enable_bing_grounding` field flows through `dsf new` → `_provision_azure_command` → the existing `enableBingGrounding` Bicep gate. (2) `DeploymentProgressPoller` gains a wall-clock timeout (env `DSF_DEPLOY_TIMEOUT`, default 600s, `<=0` disables) that cancels the ARM deployment and raises `DeploymentTimeoutError` naming the still-running resource(s). (3) Docs.

**Tech Stack:** Python 3.12, pydantic, argparse (`BooleanOptionalAction`), pytest (offline, injected `run`/`sleep`/`monotonic`), `uv`.

**Spec:** `docs/superpowers/specs/2026-06-25-dsf-new-deploy-hardening-design.md`

**Verification gates (run from repo root):**
- `uv run pytest -q`
- `uv run ruff check .`
- `uv run lint-imports` (expect "4 kept, 0 broken")

---

## File Structure

- `cli/src/dsf/instance/spec.py` — add `enable_bing_grounding: bool = True` to `InstanceSpec`.
- `cli/src/dsf/instance/provisioner.py` — `_provision_azure_command` appends `enableBingGrounding={true|false}`.
- `cli/src/dsf/cli/factory.py` — add `--enable-bing-grounding/--no-enable-bing-grounding`; thread into `InstanceSpec`.
- `cli/src/dsf/instance/deploy_progress.py` — `DeploymentTimeoutError`, `_resolve_timeout`, injected `monotonic`, timeout + cancel in `stream`.
- `cli/tests/instance/test_provisioner.py` — param tests.
- `cli/tests/cli/test_factory.py` — flag parse + plumbing tests.
- `cli/tests/instance/test_deploy_progress.py` — timeout/cancel/disable tests.
- `docs/site/get-started/provision-a-factory.md` — recovery note.

---

## Task 1: `enable_bing_grounding` spec field + provisioner param

**Files:**
- Modify: `cli/src/dsf/instance/spec.py` (`InstanceSpec`, ~line 41)
- Modify: `cli/src/dsf/instance/provisioner.py` (`_provision_azure_command`, ~lines 690-699)
- Test: `cli/tests/instance/test_provisioner.py`

- [ ] **Step 1: Write the failing tests**

Add to `cli/tests/instance/test_provisioner.py` (after `test_provision_azure_passes_github_repository_param`, ~line 143). `InstanceSpec` is already imported at line 17.

```python
def test_provision_azure_enables_bing_grounding_by_default():
    plan = InstanceProvisioner(_spec()).plan()
    step = next(s for s in plan.steps if s.name == "provision_azure")
    assert "enableBingGrounding=true" in step.command


def test_provision_azure_disables_bing_grounding_when_spec_opts_out():
    spec = InstanceSpec(product="demo", owner="acme", enable_bing_grounding=False)
    plan = InstanceProvisioner(spec).plan()
    step = next(s for s in plan.steps if s.name == "provision_azure")
    assert "enableBingGrounding=false" in step.command
    assert "enableBingGrounding=true" not in step.command
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest cli/tests/instance/test_provisioner.py::test_provision_azure_enables_bing_grounding_by_default cli/tests/instance/test_provisioner.py::test_provision_azure_disables_bing_grounding_when_spec_opts_out -q`
Expected: FAIL — `enable_bing_grounding` is not a valid `InstanceSpec` field / `enableBingGrounding=...` not in command.

- [ ] **Step 3: Add the spec field**

In `cli/src/dsf/instance/spec.py`, inside `InstanceSpec`, add the field after `location` (line 41):

```python
    location: str = "swedencentral"
    enable_bing_grounding: bool = True
```

- [ ] **Step 4: Pass the param in the provisioner**

In `cli/src/dsf/instance/provisioner.py`, in `_provision_azure_command`, add to the `params` list (after the `githubRepository=...` entry, ~line 698):

```python
            f"githubRepository={s.github_repo()}",
            f"enableBingGrounding={'true' if s.enable_bing_grounding else 'false'}",
        ]
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -q -k bing_grounding`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/instance/spec.py cli/src/dsf/instance/provisioner.py cli/tests/instance/test_provisioner.py
git commit -m "feat: thread enableBingGrounding from InstanceSpec into the deploy command

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 2: `--enable-bing-grounding/--no-...` flag on `dsf new`

**Files:**
- Modify: `cli/src/dsf/cli/factory.py` (`build_parser` `p_new`, ~line 429; `_cmd_new` `InstanceSpec(...)`, ~line 219)
- Test: `cli/tests/cli/test_factory.py`

- [ ] **Step 1: Write the failing tests**

In `cli/tests/cli/test_factory.py`, extend the shared `_CapturingProvisioner` (~line 534) to also record the spec — change its `__init__` to:

```python
class _CapturingProvisioner:
    """Records the kwargs `dsf new` threads into InstanceProvisioner."""

    last_kwargs: dict = {}
    last_spec = None

    def __init__(self, spec, repo_root=None, **kwargs):
        type(self).last_kwargs = kwargs
        type(self).last_spec = spec
        self.spec = spec

    def plan(self):
        return InstancePlan(product=self.spec.product, steps=[])
```

Then add these tests (near `test_new_parser_wiring`, after line 43):

```python
def test_new_parser_bing_grounding_default_and_opt_out():
    on = build_parser().parse_args(["new", "--product", "demo", "--owner", "acme"])
    assert on.enable_bing_grounding is True
    off = build_parser().parse_args(
        ["new", "--product", "demo", "--owner", "acme", "--no-enable-bing-grounding"]
    )
    assert off.enable_bing_grounding is False


def test_new_threads_bing_grounding_opt_out_into_spec(tmp_path, monkeypatch):
    from dsf.instance import provisioner as prov_mod

    monkeypatch.setattr(prov_mod, "InstanceProvisioner", _CapturingProvisioner)
    rc = main([
        "new", "--product", "demo", "--owner", "acme", "--name-prefix", "demopfx",
        "--dry-run", "--config-root", str(tmp_path), "--no-enable-bing-grounding",
    ])
    assert rc == 0
    assert _CapturingProvisioner.last_spec.enable_bing_grounding is False
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest cli/tests/cli/test_factory.py -q -k bing_grounding`
Expected: FAIL — `argparse.Namespace` has no `enable_bing_grounding` (parse uses default attr error) / `last_spec.enable_bing_grounding` AttributeError.

- [ ] **Step 3: Add the argparse flag**

In `cli/src/dsf/cli/factory.py`, in `build_parser`, add after the `--creation-maturity` argument block (after line 429, before `--dry-run`):

```python
    p_new.add_argument(
        "--enable-bing-grounding",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="provision Grounding with Bing Search (a Foundry project + connection) for "
        "the WebIQ source agent; pass --no-enable-bing-grounding to skip it (e.g. the "
        "tenant blocks the Microsoft.Bing provider, or the Foundry connection deploy is "
        "flaky)",
    )
```

(`import argparse` is already present at line 11.)

- [ ] **Step 4: Thread the flag into the spec**

In `cli/src/dsf/cli/factory.py`, in `_cmd_new`, add to the `InstanceSpec(...)` constructor (after `creation_maturity=args.creation_maturity,`, ~line 219):

```python
        creation_maturity=args.creation_maturity,
        enable_bing_grounding=args.enable_bing_grounding,
    )
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_factory.py -q -k bing_grounding`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/cli/factory.py cli/tests/cli/test_factory.py
git commit -m "feat: add dsf new --no-enable-bing-grounding to skip Bing grounding

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 3: Bounded deploy poller (timeout + cancel)

**Files:**
- Modify: `cli/src/dsf/instance/deploy_progress.py` (imports; new constants/error/`_resolve_timeout`; `DeploymentProgressPoller.__init__` + `stream`; new `_timed_out`/`_cancel`/`_running_summary`)
- Test: `cli/tests/instance/test_deploy_progress.py`

- [ ] **Step 1: Write the failing tests**

In `cli/tests/instance/test_deploy_progress.py`, update the imports (lines 10-18) to add the new symbols:

```python
from dsf.instance.deploy_progress import (
    _DEFAULT_POLL_INTERVAL,
    _DEFAULT_TIMEOUT,
    DeploymentFailedError,
    DeploymentProgressPoller,
    DeploymentTimeoutError,
    _format_duration,
    _parse_json,
    _resolve_poll_interval,
    _resolve_timeout,
    _status_message,
)
```

Replace the `_poller` helper (lines 53-56) with a version that accepts optional `timeout`/`monotonic`, and add a clock stub:

```python
def _poller(run, emit, *, timeout=None, monotonic=None):
    kwargs = {"run": run, "sleep": lambda *_a: None, "emit": emit, "interval": 0}
    if timeout is not None:
        kwargs["timeout"] = timeout
    if monotonic is not None:
        kwargs["monotonic"] = monotonic
    return DeploymentProgressPoller(**kwargs)


def _stub_clock(*values):
    """A ``monotonic()`` stub: yields each value once, then repeats the last."""
    seq = list(values)

    def _now():
        return seq.pop(0) if len(seq) > 1 else seq[0]

    return _now
```

Add these tests at the end of the file:

```python
def test_stream_times_out_cancels_and_names_wedged_resource():
    conn = {
        "resourceType": "Microsoft.CognitiveServices/accounts/projects/connections",
        "resourceName": "aif/proj/bing-conn",
    }
    ops = [{"properties": {"provisioningState": "Running", "targetResource": conn}}]
    calls: list[list[str]] = []

    def run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:5] == ["az", "deployment", "operation", "group", "list"]:
            return MagicMock(returncode=0, stdout=json.dumps(ops))
        if cmd[:4] == ["az", "deployment", "group", "show"]:
            return MagicMock(returncode=0, stdout="Running")  # never terminal
        return MagicMock(returncode=0, stdout="")

    with pytest.raises(DeploymentTimeoutError) as excinfo:
        _poller(
            run, lambda _l: None, timeout=600,
            monotonic=_stub_clock(0.0, 50.0, 700.0),
        ).stream("rg-x", "dep-x")
    msg = str(excinfo.value)
    assert "bing-conn" in msg
    assert "600s" in msg
    assert ["az", "deployment", "group", "cancel", "-g", "rg-x", "-n", "dep-x"] in calls


def test_stream_timeout_tolerates_cancel_failure():
    ops = [{"properties": {
        "provisioningState": "Running",
        "targetResource": {"resourceType": "T", "resourceName": "r"},
    }}]

    def run(cmd, **kwargs):
        if cmd[:4] == ["az", "deployment", "group", "cancel"]:
            raise RuntimeError("cancel boom")
        if cmd[:5] == ["az", "deployment", "operation", "group", "list"]:
            return MagicMock(returncode=0, stdout=json.dumps(ops))
        if cmd[:4] == ["az", "deployment", "group", "show"]:
            return MagicMock(returncode=0, stdout="Running")
        return MagicMock(returncode=0, stdout="")

    with pytest.raises(DeploymentTimeoutError):
        _poller(
            run, lambda _l: None, timeout=600, monotonic=_stub_clock(0.0, 700.0)
        ).stream("rg-x", "dep-x")


def test_stream_success_before_timeout_does_not_cancel():
    calls: list[list[str]] = []

    def run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:5] == ["az", "deployment", "operation", "group", "list"]:
            return MagicMock(returncode=0, stdout="[]")
        if cmd[:4] == ["az", "deployment", "group", "show"]:
            query = cmd[cmd.index("--query") + 1]
            if query == "properties.provisioningState":
                return MagicMock(returncode=0, stdout="Succeeded")
            if query == "properties.outputs":
                return MagicMock(returncode=0, stdout="{}")
        return MagicMock(returncode=0, stdout="")

    out = _poller(
        run, lambda _l: None, timeout=600, monotonic=_stub_clock(0.0, 1.0)
    ).stream("rg-x", "dep-x")
    assert out == {}
    assert not any(c[:4] == ["az", "deployment", "group", "cancel"] for c in calls)


def test_stream_timeout_disabled_when_nonpositive():
    op_run = [{"properties": {
        "provisioningState": "Running",
        "targetResource": {"resourceType": "T", "resourceName": "r"},
    }}]
    op_done = [{"properties": {
        "provisioningState": "Succeeded",
        "targetResource": {"resourceType": "T", "resourceName": "r"},
    }}]
    out = _poller(
        _ScriptedAz([op_run, op_done], ["Running", "Succeeded"]),
        lambda _l: None, timeout=0, monotonic=_stub_clock(0.0, 9999.0),
    ).stream("rg-x", "dep-x")
    assert out == {}


def test_resolve_timeout_env_override_and_disable(monkeypatch):
    monkeypatch.delenv("DSF_DEPLOY_TIMEOUT", raising=False)
    assert _resolve_timeout() == _DEFAULT_TIMEOUT
    monkeypatch.setenv("DSF_DEPLOY_TIMEOUT", "120")
    assert _resolve_timeout() == 120.0
    monkeypatch.setenv("DSF_DEPLOY_TIMEOUT", "0")
    assert _resolve_timeout() == 0.0  # disables the bound
    monkeypatch.setenv("DSF_DEPLOY_TIMEOUT", "garbage")
    assert _resolve_timeout() == _DEFAULT_TIMEOUT
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest cli/tests/instance/test_deploy_progress.py -q`
Expected: FAIL on import — `DeploymentTimeoutError`, `_DEFAULT_TIMEOUT`, `_resolve_timeout` are not defined.

- [ ] **Step 3: Add the import, constants, error, and `_resolve_timeout`**

In `cli/src/dsf/instance/deploy_progress.py`, add `import time` to the imports (after `import re`, line 19):

```python
import os
import re
import time
```

Add a constant after `_DEFAULT_POLL_INTERVAL` (line 35):

```python
#: Wall-clock cap on a single ``stream`` run. ``DSF_DEPLOY_TIMEOUT`` (seconds)
#: overrides it; a value <= 0 disables the bound (wait indefinitely).
_DEFAULT_TIMEOUT = 600.0
```

Add the error next to `DeploymentFailedError` (after line 50):

```python
class DeploymentTimeoutError(RuntimeError):
    """A deployment stayed non-terminal past the poll timeout.

    ``str()`` names the still-running resource operation(s) so the provisioner's
    ``_format_step_error`` surfaces which resource wedged (e.g. a Foundry/Bing
    grounding connection whose secret materialization 500s server-side).
    """
```

Add the resolver next to `_resolve_poll_interval` (after line 61):

```python
def _resolve_timeout() -> float:
    """Deploy timeout in seconds: ``DSF_DEPLOY_TIMEOUT`` env > ``_DEFAULT_TIMEOUT``.

    A value ``<= 0`` is returned as-is and disables the bound in
    :meth:`DeploymentProgressPoller._timed_out` (wait indefinitely).
    """
    raw = os.environ.get("DSF_DEPLOY_TIMEOUT", "").strip()
    if raw:
        try:
            return float(raw)
        except ValueError:
            pass
    return _DEFAULT_TIMEOUT
```

- [ ] **Step 4: Inject the clock + timeout into the poller and enforce it in `stream`**

In `DeploymentProgressPoller.__init__`, add the two parameters and store them. Replace the constructor signature/body (lines 158-173) with:

```python
    def __init__(
        self,
        *,
        run: Runner,
        sleep: Callable[[float], None],
        emit: Callable[[str], None] | None = None,
        interval: float | None = None,
        timeout: float | None = None,
        monotonic: Callable[[], float] = time.monotonic,
    ) -> None:
        self._run = run
        self._sleep = sleep
        self._emit = emit or (lambda _line: None)
        self._interval = (
            max(_MIN_POLL_INTERVAL, interval)
            if interval is not None
            else _resolve_poll_interval()
        )
        self._timeout = _resolve_timeout() if timeout is None else timeout
        self._monotonic = monotonic
```

Replace the `stream` method body (lines 175-193) with the timeout-aware loop:

```python
    def stream(self, resource_group: str, deployment_name: str) -> dict[str, Any]:
        """Drive the deployment to a terminal state, emitting progress lines.

        Returns the deployment's parsed ``properties.outputs`` on success; raises
        :class:`DeploymentFailedError` on a non-``Succeeded`` terminal state, or
        :class:`DeploymentTimeoutError` if it stays non-terminal past the timeout
        (after best-effort cancelling the deployment).
        """
        seen: dict[str, str] = {}
        ops: list[dict[str, Any]] = []
        state: str | None = None
        start = self._monotonic()
        while state not in _TERMINAL_STATES:
            if state is not None:
                self._sleep(self._interval)
            ops = self._list_operations(resource_group, deployment_name)
            self._emit_changes(ops, seen)
            state = self._deployment_state(resource_group, deployment_name)
            if state not in _TERMINAL_STATES and self._timed_out(start):
                self._cancel(resource_group, deployment_name)
                raise DeploymentTimeoutError(
                    f"deployment {deployment_name} did not reach a terminal state "
                    f"within {self._timeout:.0f}s; canceled. Still running: "
                    f"{self._running_summary(ops)}"
                )
        if state == "Succeeded":
            return self._fetch_outputs(resource_group, deployment_name)
        detail = self._failure_summary(resource_group, deployment_name, ops)
        raise DeploymentFailedError(f"deployment {deployment_name} {state}: {detail}")
```

- [ ] **Step 5: Add the `_timed_out`, `_cancel`, and `_running_summary` helpers**

In `cli/src/dsf/instance/deploy_progress.py`, add these methods to `DeploymentProgressPoller` (place them after `stream`, before `_list_operations`):

```python
    def _timed_out(self, start: float) -> bool:
        """True once the wall-clock budget is spent. ``timeout <= 0`` never trips."""
        return self._timeout > 0 and (self._monotonic() - start) >= self._timeout

    def _cancel(self, rg: str, name: str) -> None:
        """Best-effort ``az deployment group cancel``; never raises.

        Frees the wedged ARM deployment so a re-run starts clean. A failed cancel
        must not mask the :class:`DeploymentTimeoutError`, so errors are swallowed.
        """
        try:
            self._run(
                ["az", "deployment", "group", "cancel", "-g", rg, "-n", name],
                check=False, capture_output=True, text=True,
            )
        except Exception:  # noqa: BLE001 - best-effort cancel on the timeout path
            pass

    def _running_summary(self, ops: list[dict[str, Any]]) -> str:
        """Label the operations still non-terminal at timeout (``type name``)."""
        running: list[str] = []
        for op in ops:
            props = op.get("properties", {}) if isinstance(op, dict) else {}
            if props.get("provisioningState") in _TERMINAL_STATES:
                continue
            target = props.get("targetResource") or {}
            key = target.get("id") or target.get("resourceName")
            if not key:
                continue  # the deployment's own / untargeted operation
            label = (
                f"{target.get('resourceType', '')} {target.get('resourceName', '')}".strip()
            )
            running.append(label or str(key))
        return ", ".join(running) if running else "(no in-flight resource named)"
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_deploy_progress.py -q`
Expected: PASS (all existing tests + the 5 new ones).

- [ ] **Step 7: Commit**

```bash
git add cli/src/dsf/instance/deploy_progress.py cli/tests/instance/test_deploy_progress.py
git commit -m "feat: bound the dsf new deploy poller with DSF_DEPLOY_TIMEOUT and cancel-on-timeout

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 4: Docs — provisioning recovery note

**Files:**
- Modify: `docs/site/get-started/provision-a-factory.md:40-45` (the "Live progress during the Azure step" note)

- [ ] **Step 1: Extend the provisioning note**

In `docs/site/get-started/provision-a-factory.md`, replace the existing admonition (lines 40-45) with:

```markdown
!!! note "Live progress during the Azure step"
    The `provision_azure` step runs `az deployment group create --no-wait` and polls the
    deployment, streaming each Azure resource as it starts and finishes (indented
    `· <type> <name>: <state>` lines) so a multi-minute deployment is never silent. On
    failure the specific failed resource and its reason are surfaced. Tune the poll cadence
    with `DSF_DEPLOY_POLL_INTERVAL` (seconds, default 5).

    The poll is **bounded** by `DSF_DEPLOY_TIMEOUT` (seconds, default 600 = 10 min; set
    `<= 0` to wait indefinitely). If the deployment is still running at the bound, `dsf new`
    cancels it and fails the step naming the still-running resource(s) — rather than hanging.
    A Foundry **Grounding with Bing Search** connection occasionally wedges here on a
    transient Azure 500 while storing its key; re-run `dsf new`, or skip it with
    `dsf new --no-enable-bing-grounding` (the WebIQ agent then runs without web research).
```

- [ ] **Step 2: Commit**

```bash
git add docs/site/get-started/provision-a-factory.md
git commit -m "docs: document DSF_DEPLOY_TIMEOUT and --no-enable-bing-grounding recovery

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 5: Full verification gates

- [ ] **Step 1: Run the full suite + lint + import boundaries**

```bash
uv run pytest -q && uv run ruff check . && uv run lint-imports
```

Expected: pytest all green (existing total + 9 new tests), ruff "All checks passed!", lint-imports "Contracts: 4 kept, 0 broken".

- [ ] **Step 2: Sanity-check the CLI help renders the new flag**

Run: `uv run dsf new --help`
Expected: shows `--enable-bing-grounding`, `--no-enable-bing-grounding`.

- [ ] **Step 3: If anything fails, fix and re-run before declaring done.**

---

## Self-review notes

- **Spec coverage:** Bing opt-out (Task 1+2), bounded poller w/ cancel + named resource (Task 3), 10-min default + env override + `<=0` disable (Task 3), docs (Task 4). All spec sections covered. The spec's "Docs" section names `operate.md`; the correct home is `provision-a-factory.md` (where the poll + `DSF_DEPLOY_POLL_INTERVAL` are already documented) — plan targets the latter.
- **Type consistency:** `enable_bing_grounding` (snake_case field) ↔ `enableBingGrounding` (Bicep param) used consistently. New poller methods `_timed_out`/`_cancel`/`_running_summary` and `_resolve_timeout`/`_DEFAULT_TIMEOUT`/`DeploymentTimeoutError` referenced identically in tests and source.
- **No placeholders:** every code/test step shows complete code and exact commands.
