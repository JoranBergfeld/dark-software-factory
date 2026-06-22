# Float per-resource `provision_azure` progress — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stream live per-resource progress to the console while step 5 (`provision_azure`) of
`dsf new` runs its multi-minute `az deployment group create`, and surface the specific failed
resource on failure.

**Architecture:** Switch `provision_azure` from a blocking, output-captured
`az deployment group create … --query properties.outputs -o json` to a `--no-wait` create plus a
new `DeploymentProgressPoller` that polls the deployment's operation list, emits a line per
resource state change, fetches outputs from `az deployment group show` on success, and raises
`DeploymentFailedError` (carrying the failed operation's reason) otherwise. Progress flows through
a new, additive `on_progress` channel threaded `factory._cmd_new → Provisioner.apply →
_execute_step → poller.emit`; the existing `on_event` step contract and outputs parsing are
unchanged.

**Tech Stack:** Python 3.12, `uv`, pytest (`asyncio_mode=auto`, namespace packages), `az` CLI
invoked through the provisioner's injected `run`/`sleep` callables (tests stay offline with
scripted JSON). ruff (E,F,I,UP,B; line length 100). Spec:
`docs/superpowers/specs/2026-06-22-float-provision-progress-design.md`.

---

## File Structure

- **Create** `cli/src/dsf/instance/deploy_progress.py` — the `DeploymentProgressPoller` unit,
  `DeploymentFailedError`, `_resolve_poll_interval`, and the line/duration/failure formatters. One
  responsibility: drive an in-flight ARM deployment to terminal and stream per-resource changes.
  Depends only on `json`, `os`, `re`, `typing`, and the injected `run`/`sleep`.
- **Create** `cli/tests/instance/test_deploy_progress.py` — unit tests for the poller (scripted
  `run` + fake `sleep`).
- **Modify** `cli/src/dsf/instance/provisioner.py` — `plan()` provision_azure command (`--no-wait`,
  drop `--query/-o json`); `apply`/`_execute_step` gain an `on_progress` channel; the
  `provision_azure` branch drives the poller; refactor `_azure_result(proc)` →
  `_azure_result_from_outputs(parsed)`.
- **Modify** `cli/tests/instance/test_provisioner.py` — update the command-shape test; add a shared
  `_az_deploy` fake-response helper; migrate the execute-path tests to the no-wait+poll+show
  sequence; rewrite the three failure tests to drive a `Failed` deployment; add an `on_progress`
  forwarding test.
- **Modify** `cli/src/dsf/cli/factory.py` — add `_print_step_progress`; pass `on_progress` from
  `_cmd_new`.
- **Modify** `cli/tests/cli/test_factory.py` — widen the `_FailingProvisioner.apply` signature;
  add an indentation test for streamed progress.
- **Modify** `docs/RUNBOOK.md` and `infra/README.md` — one note each about live step-5 progress and
  `DSF_DEPLOY_POLL_INTERVAL`.

---

## Task 1: `DeploymentProgressPoller` (new module)

**Files:**
- Create: `cli/src/dsf/instance/deploy_progress.py`
- Test: `cli/tests/instance/test_deploy_progress.py`

- [ ] **Step 1: Write the failing tests**

Create `cli/tests/instance/test_deploy_progress.py`:

```python
"""Tests for DeploymentProgressPoller (offline, scripted `az` responses)."""

from __future__ import annotations

import json
from unittest.mock import MagicMock

import pytest

from dsf.instance.deploy_progress import (
    _DEFAULT_POLL_INTERVAL,
    DeploymentFailedError,
    DeploymentProgressPoller,
    _format_duration,
    _resolve_poll_interval,
)


class _ScriptedAz:
    """Fake `run` returning scripted operation-list / state / outputs payloads.

    ``op_polls[i]`` is the operation-list returned on the i-th poll and
    ``states[i]`` the deployment provisioningState read in that same iteration;
    the last entry is reused if the poller polls more times than scripted.
    """

    def __init__(self, op_polls, states, outputs="{}"):
        self._op_polls = list(op_polls)
        self._states = list(states)
        self._outputs = outputs
        self._poll = -1

    def __call__(self, cmd, **kwargs):
        if cmd[:5] == ["az", "deployment", "operation", "group", "list"]:
            self._poll += 1
            idx = min(self._poll, len(self._op_polls) - 1)
            return MagicMock(returncode=0, stdout=json.dumps(self._op_polls[idx]))
        if cmd[:4] == ["az", "deployment", "group", "show"]:
            query = cmd[cmd.index("--query") + 1]
            if query == "properties.provisioningState":
                idx = min(self._poll, len(self._states) - 1)
                return MagicMock(returncode=0, stdout=self._states[idx])
            if query == "properties.outputs":
                return MagicMock(returncode=0, stdout=self._outputs)
        return MagicMock(returncode=0, stdout="")


def _poller(run, emit):
    return DeploymentProgressPoller(
        run=run, sleep=lambda *_a: None, emit=emit, interval=0
    )


def test_stream_emits_running_then_succeeded_per_resource():
    res = {
        "resourceType": "Microsoft.DocumentDB/databaseAccounts",
        "resourceName": "cosmos-x",
    }
    op_run = [{"properties": {"provisioningState": "Running", "targetResource": res}}]
    op_done = [{
        "properties": {
            "provisioningState": "Succeeded",
            "duration": "PT1M4S",
            "targetResource": res,
        }
    }]
    lines: list[str] = []
    out = _poller(_ScriptedAz([op_run, op_done], ["Running", "Succeeded"]), lines.append).stream(
        "rg-x", "dep-x"
    )
    assert out == {}
    assert lines == [
        "· Microsoft.DocumentDB/databaseAccounts cosmos-x: … Running",
        "· Microsoft.DocumentDB/databaseAccounts cosmos-x: ✓ Succeeded (1m04s)",
    ]


def test_stream_skips_untargeted_operations():
    ops = [{"properties": {"provisioningState": "Succeeded"}}]  # the deployment's own op
    lines: list[str] = []
    _poller(_ScriptedAz([ops], ["Succeeded"]), lines.append).stream("rg-x", "dep-x")
    assert lines == []


def test_stream_returns_parsed_outputs_on_success():
    outputs = '{"appConfigEndpoint": {"type": "String", "value": "https://x.azconfig.io"}}'
    out = _poller(_ScriptedAz([[]], ["Succeeded"], outputs=outputs), lambda _l: None).stream(
        "rg-x", "dep-x"
    )
    assert out["appConfigEndpoint"]["value"] == "https://x.azconfig.io"


def test_stream_raises_with_failed_operation_reason():
    quota = "InsufficientVCPUQuota: remaining 0 for family standardDSv5Family"
    ops = [{
        "properties": {
            "provisioningState": "Failed",
            "statusMessage": {"error": {"message": quota}},
            "targetResource": {
                "resourceType": "Microsoft.App/containerApps",
                "resourceName": "dsf-orchestrator-demo",
            },
        }
    }]
    with pytest.raises(DeploymentFailedError) as excinfo:
        _poller(_ScriptedAz([ops], ["Failed"]), lambda _l: None).stream("rg-x", "dep-x")
    message = str(excinfo.value)
    assert quota in message
    assert "dsf-orchestrator-demo" in message


def test_stream_tolerates_empty_first_poll():
    res = {"resourceType": "Microsoft.KeyVault/vaults", "resourceName": "kv-x"}
    op_done = [{"properties": {"provisioningState": "Succeeded", "targetResource": res}}]
    lines: list[str] = []
    _poller(_ScriptedAz([[], op_done], ["Running", "Succeeded"]), lines.append).stream(
        "rg-x", "dep-x"
    )
    assert lines == ["· Microsoft.KeyVault/vaults kv-x: ✓ Succeeded"]


def test_format_duration_renders_iso8601():
    assert _format_duration("PT1M4S") == "1m04s"
    assert _format_duration("PT45S") == "45s"
    assert _format_duration("PT2H3M4S") == "2h03m"
    assert _format_duration(None) == ""
    assert _format_duration("garbage") == ""


def test_resolve_poll_interval_env_override_and_floor(monkeypatch):
    monkeypatch.delenv("DSF_DEPLOY_POLL_INTERVAL", raising=False)
    assert _resolve_poll_interval() == _DEFAULT_POLL_INTERVAL
    monkeypatch.setenv("DSF_DEPLOY_POLL_INTERVAL", "0.2")
    assert _resolve_poll_interval() == 1.0  # floored to 1s
    monkeypatch.setenv("DSF_DEPLOY_POLL_INTERVAL", "12")
    assert _resolve_poll_interval() == 12.0
    monkeypatch.setenv("DSF_DEPLOY_POLL_INTERVAL", "bad")
    assert _resolve_poll_interval() == _DEFAULT_POLL_INTERVAL
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest cli/tests/instance/test_deploy_progress.py -q`
Expected: FAIL — `ModuleNotFoundError: No module named 'dsf.instance.deploy_progress'`.

- [ ] **Step 3: Write the implementation**

Create `cli/src/dsf/instance/deploy_progress.py`:

```python
"""Stream live per-resource progress for a ``provision_azure`` ARM deployment.

Step 5 of ``dsf new`` runs one ``az deployment group create`` over
``infra/main.bicep`` that stamps 15+ resources and takes minutes. Run with
``--no-wait`` and polled here, the individual resource operations show up on the
console as they start and finish — and a failure names the specific resource that
broke instead of one opaque ``DeploymentFailed`` blob.

Azure is reached only through the injected ``run`` callable (a
``subprocess.run``-compatible signature) and an injected ``sleep``, so tests stay
offline with scripted JSON — mirroring
:class:`dsf.instance.provisioner.InstanceProvisioner`.
"""

from __future__ import annotations

import json
import os
import re
from collections.abc import Callable
from typing import Any

Runner = Callable[..., Any]

#: Deployment-level provisioning states that end the poll loop.
_TERMINAL_STATES = {"Succeeded", "Failed", "Canceled"}

#: Per-resource states that mark a failed operation (for the failure message).
_FAILED_STATES = {"Failed", "Canceled"}

#: Poll cadence default: how often to re-list the deployment's operations.
_DEFAULT_POLL_INTERVAL = 5.0

#: ISO-8601 duration as Azure emits for an operation (e.g. ``PT1M4S``).
_DURATION_RE = re.compile(r"PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)(?:\.\d+)?S)?$")


class DeploymentFailedError(RuntimeError):
    """An ARM deployment reached a non-``Succeeded`` terminal state.

    ``str()`` names the failed resource operation(s) and their status message so
    the provisioner's ``_format_step_error`` surfaces the real reason.
    """


def _resolve_poll_interval() -> float:
    """Poll cadence in seconds: ``DSF_DEPLOY_POLL_INTERVAL`` env > 5s, min 1s."""
    raw = os.environ.get("DSF_DEPLOY_POLL_INTERVAL", "").strip()
    if raw:
        try:
            return max(1.0, float(raw))
        except ValueError:
            pass
    return _DEFAULT_POLL_INTERVAL


def _format_duration(raw: Any) -> str:
    """Render an Azure ISO-8601 operation duration as ``1m04s`` / ``45s``.

    Returns ``""`` when the duration is absent or unparseable so the caller omits
    it from the line.
    """
    if not isinstance(raw, str):
        return ""
    match = _DURATION_RE.fullmatch(raw.strip())
    if not match:
        return ""
    hours, minutes, seconds = (int(g) if g else 0 for g in match.groups())
    if hours:
        return f"{hours}h{minutes:02d}m"
    if minutes:
        return f"{minutes}m{seconds:02d}s"
    return f"{seconds}s"


def _status_message(status: Any) -> str:
    """Pull a readable reason out of an operation's ``statusMessage``.

    Azure sets it to either a plain string or a nested
    ``{"status": ..., "error": {"code": ..., "message": ...}}`` envelope.
    """
    if isinstance(status, dict):
        error = status.get("error")
        if isinstance(error, dict):
            message = error.get("message")
            if isinstance(message, str) and message.strip():
                return message.strip()
        inner = status.get("status")
        if isinstance(inner, str) and inner.strip():
            return inner.strip()
        return json.dumps(status)
    if isinstance(status, str):
        return status.strip()
    return ""


def _format_line(target: dict[str, Any], state: str, props: dict[str, Any]) -> str:
    symbol = {"Succeeded": "✓", "Failed": "✗", "Canceled": "✗"}.get(state, "…")
    label = f"{target.get('resourceType', '')} {target.get('resourceName', '')}".strip()
    line = f"· {label}: {symbol} {state}".rstrip()
    duration = _format_duration(props.get("duration"))
    if duration:
        line += f" ({duration})"
    return line


class DeploymentProgressPoller:
    """Polls an in-flight ARM deployment and streams per-resource state changes.

    Parameters
    ----------
    run:
        ``subprocess.run``-compatible callable used for every ``az`` call.
    sleep:
        Sleep callable invoked between polls (injected for tests).
    emit:
        Sink for each formatted progress line. Defaults to a no-op.
    interval:
        Seconds between operation-list polls; falls back to
        ``DSF_DEPLOY_POLL_INTERVAL`` (default 5s) when ``None``.
    """

    def __init__(
        self,
        *,
        run: Runner,
        sleep: Callable[[float], None],
        emit: Callable[[str], None] | None = None,
        interval: float | None = None,
    ) -> None:
        self._run = run
        self._sleep = sleep
        self._emit = emit or (lambda _line: None)
        self._interval = interval if interval is not None else _resolve_poll_interval()

    def stream(self, resource_group: str, deployment_name: str) -> dict[str, Any]:
        """Drive the deployment to a terminal state, emitting progress lines.

        Returns the deployment's parsed ``properties.outputs`` on success; raises
        :class:`DeploymentFailedError` otherwise.
        """
        seen: dict[str, str] = {}
        ops: list[dict[str, Any]] = []
        state: str | None = None
        while state not in _TERMINAL_STATES:
            if state is not None:
                self._sleep(self._interval)
            ops = self._list_operations(resource_group, deployment_name)
            self._emit_changes(ops, seen)
            state = self._deployment_state(resource_group, deployment_name)
        if state == "Succeeded":
            return self._fetch_outputs(resource_group, deployment_name)
        raise DeploymentFailedError(
            f"deployment {deployment_name} {state}: {self._failure_detail(ops)}"
        )

    def _list_operations(self, rg: str, name: str) -> list[dict[str, Any]]:
        proc = self._run(
            [
                "az", "deployment", "operation", "group", "list",
                "-g", rg, "-n", name, "-o", "json",
            ],
            check=True, capture_output=True, text=True,
        )
        return _parse_json(getattr(proc, "stdout", ""), list)

    def _deployment_state(self, rg: str, name: str) -> str:
        proc = self._run(
            [
                "az", "deployment", "group", "show",
                "-g", rg, "-n", name,
                "--query", "properties.provisioningState", "-o", "tsv",
            ],
            check=True, capture_output=True, text=True,
        )
        return (getattr(proc, "stdout", "") or "").strip()

    def _fetch_outputs(self, rg: str, name: str) -> dict[str, Any]:
        proc = self._run(
            [
                "az", "deployment", "group", "show",
                "-g", rg, "-n", name,
                "--query", "properties.outputs", "-o", "json",
            ],
            check=True, capture_output=True, text=True,
        )
        return _parse_json(getattr(proc, "stdout", ""), dict)

    def _emit_changes(self, ops: list[dict[str, Any]], seen: dict[str, str]) -> None:
        for op in ops:
            props = op.get("properties", {}) if isinstance(op, dict) else {}
            target = props.get("targetResource") or {}
            key = target.get("id") or target.get("resourceName")
            if not key:
                continue  # the deployment's own / untargeted operation
            state = props.get("provisioningState", "") or ""
            if seen.get(key) == state:
                continue
            seen[key] = state
            self._emit(_format_line(target, state, props))

    def _failure_detail(self, ops: list[dict[str, Any]]) -> str:
        failures: list[str] = []
        for op in ops:
            props = op.get("properties", {}) if isinstance(op, dict) else {}
            if props.get("provisioningState") not in _FAILED_STATES:
                continue
            target = props.get("targetResource") or {}
            label = (
                f"{target.get('resourceType', '')} {target.get('resourceName', '')}".strip()
                or "(deployment)"
            )
            reason = _status_message(props.get("statusMessage"))
            failures.append(f"{label}: {reason}" if reason else label)
        return "; ".join(failures) if failures else "(no per-operation detail)"


def _parse_json(raw: Any, expected: type) -> Any:
    """``json.loads`` ``raw`` when it parses to ``expected``; else an empty one."""
    text = raw or ""
    try:
        parsed = json.loads(text) if isinstance(text, str) and text.strip() else expected()
    except json.JSONDecodeError:
        parsed = expected()
    return parsed if isinstance(parsed, expected) else expected()
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_deploy_progress.py -q`
Expected: PASS (7 passed).

- [ ] **Step 5: Lint**

Run: `uv run ruff check cli/src/dsf/instance/deploy_progress.py cli/tests/instance/test_deploy_progress.py`
Expected: `All checks passed!`

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/instance/deploy_progress.py cli/tests/instance/test_deploy_progress.py
git commit -m "feat(cli): DeploymentProgressPoller for live provision_azure progress"
```

---

## Task 2: Wire the poller into the provisioner (no-wait create + poll)

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py`
- Test: `cli/tests/instance/test_provisioner.py`

> ⚠️ **Hang hazard — migrate EVERY `apply(execute=True)` test.** After this change the
> `provision_azure` branch runs the poller, which calls `az deployment group show --query
> properties.provisioningState`. If a test's fake `run` doesn't return a real terminal-state
> string for that call, `_deployment_state` returns a non-terminal value (e.g. a truthy
> `MagicMock`) and the poll loop spins forever on a real `time.sleep`. Route the
> az-deployment calls in **every** execute-path test through `_az_deploy` (Step 3). The full
> list of `apply(execute=True)` tests is enumerated in Step 3 — do not skip any.

- [ ] **Step 1: Update the command-shape test (will fail)**

In `cli/tests/instance/test_provisioner.py`, replace the two `--query`/`-o json` assertions at the
end of `test_plan_provision_azure_command_shape` (currently the last two lines of that test):

```python
    assert az.command[az.command.index("--query") + 1] == "properties.outputs"
    assert az.command[-2:] == ["-o", "json"]
```

with:

```python
    assert "--no-wait" in az.command
    assert "--query" not in az.command  # outputs are fetched post-deploy via `show`
```

- [ ] **Step 2: Add the shared `_az_deploy` helper and a JSON import**

In `cli/tests/instance/test_provisioner.py`, add `import json` to the imports (next to
`import subprocess` near the top), then add this module-level helper after the
`_AZURE_OUTPUTS_JSON` definition (around line 31):

```python
def _az_deploy(cmd, outputs_json, *, state="Succeeded", ops_json=None):
    """Respond to the no-wait create + poll + show calls; ``None`` for other cmds.

    Lets each execute-path test delegate the provision_azure az-sequence to one
    place: the ``create`` accepts (no output), the operation-list returns ``ops_json``
    (a single Succeeded App Config op by default), and ``show`` returns ``state`` for
    the provisioningState query and ``outputs_json`` for the outputs query.
    """
    if cmd[:4] == ["az", "deployment", "group", "create"]:
        return MagicMock(returncode=0, stdout="")
    if cmd[:5] == ["az", "deployment", "operation", "group", "list"]:
        default_ops = json.dumps([
            {"properties": {
                "provisioningState": "Succeeded",
                "duration": "PT5S",
                "targetResource": {
                    "resourceType": "Microsoft.AppConfiguration/configurationStores",
                    "resourceName": "demo-appconfig",
                },
            }}
        ])
        return MagicMock(returncode=0, stdout=ops_json if ops_json is not None else default_ops)
    if cmd[:4] == ["az", "deployment", "group", "show"]:
        query = cmd[cmd.index("--query") + 1] if "--query" in cmd else ""
        if query == "properties.provisioningState":
            return MagicMock(returncode=0, stdout=state)
        if query == "properties.outputs":
            return MagicMock(returncode=0, stdout=outputs_json)
        return MagicMock(returncode=0, stdout="")
    return None
```

- [ ] **Step 3: Migrate EVERY success-path execute test to the new sequence**

Every `apply(execute=True)` test reaches the `provision_azure` branch and now drives the poller.
There are two shapes to migrate; **do not skip any** (an un-migrated test hangs — see the warning
above). After migrating, the poller polls operations + `show provisioningState` (Succeeded on the
first poll, so no `sleep` is called) + `show properties.outputs`.

**Shape A — the test has an explicit `az deployment group create` branch.** Replace it:

```python
        if cmd[:4] == ["az", "deployment", "group", "create"]:
            return MagicMock(returncode=0, stdout=<OUTPUTS>)
```

with a delegation to `_az_deploy`, keeping the rest of the function intact:

```python
        hit = _az_deploy(cmd, <OUTPUTS>)
        if hit is not None:
            return hit
```

Pass exactly this `<OUTPUTS>` per test (verified against the current source — the SRE/captures
tests rely on their **local** `outputs_json`, not the module constant):

| Test (def) | Line | `<OUTPUTS>` to pass |
| --- | --- | --- |
| `test_apply_execute_registers_product_into_routing_registry` | ~180 | `_AZURE_OUTPUTS_JSON` |
| `test_apply_execute_runs_real_steps_and_onboards_sre` | ~217 | `_AZURE_OUTPUTS_JSON` |
| `test_apply_execute_captures_azure_outputs` | ~297 | `outputs_json` (its local var) |
| `test_apply_execute_emits_start_and_done_events_per_step` | ~379 | `_AZURE_OUTPUTS_JSON` |
| `test_apply_dry_run_preserves_prior_azure_outputs` | ~431 | `outputs_json` (its local var; the fn is named `exec_run`) |
| `test_apply_execute_aca_updates_container_app` | ~453 | `_AZURE_OUTPUTS_JSON` |
| `test_deploy_squad_ralph_applies_manifests_on_execute` | ~506 | `_AZURE_OUTPUTS_JSON` |
| `test_deploy_sre_agent_executes_sub_deployment` | ~591 | `outputs_json` (its local var) |
| `test_deploy_sre_agent_connect_repo_skipped_when_no_endpoint` | ~623 | `outputs_json` (its local var) |
| `test_deploy_sre_agent_connect_repo_skipped_when_no_gh_token` | ~651 | `outputs_json` (its local var) |
| `test_deploy_sre_agent_connect_repo_calls_az_rest_when_token_present` | ~679 | `outputs_json` (its local var) |
| `test_seed_appconfig_seeds_from_deployment_endpoint_on_execute` | ~726 | `_AZURE_OUTPUTS_JSON` |
| `test_seed_appconfig_retries_until_rbac_propagates` | ~767 | `_AZURE_OUTPUTS_JSON` |
| `test_seed_appconfig_fails_when_no_endpoint_in_outputs` | ~792 | `'{"cosmosEndpoint": {"type": "String", "value": "x"}}'` (the inline literal that branch already returns) |

Notes:
- `test_deploy_squad_ralph_applies_manifests_on_execute` returns `subprocess.CompletedProcess`
  objects, not `MagicMock`, and wraps its `run` in `MagicMock(side_effect=run)`. Replace only the
  `create` branch inside the inner `run`; the `_az_deploy` `MagicMock` return mixes fine (the
  provisioner only reads `.returncode`/`.stdout`), and the extra `operation group list` / `show`
  calls land harmlessly in `run.call_args_list`.
- `test_seed_appconfig_retries_until_rbac_propagates` already injects `sleep=sleeps.append`. Leave
  it: the poller is `Succeeded` on the first poll and never sleeps, so the
  `sleeps == [_SEED_RETRY_DELAY, _SEED_RETRY_DELAY]` assertion (seed backoff only) still holds.

**Shape B — the test has NO `create` branch, only a catch-all** (these return a bare
`MagicMock(returncode=...)` for everything and would otherwise feed the poller a truthy-MagicMock
state → infinite loop). Insert the delegation immediately **before** the final catch-all `return`,
so `gh repo view` etc. still fall through unchanged:

```python
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=...)  # the test's existing catch-all, unchanged
```

Apply Shape B to:
- `test_apply_execute_creates_labels` (~125) — keep its `returncode = 1 if cmd[:3] == [...] else 0`
  catch-all line; insert the delegation just above `return MagicMock(returncode=returncode)`.
- `test_apply_execute_skips_clone_when_repo_and_local_dir_exist` (~253)
- `test_apply_execute_clones_when_repo_exists_but_not_local` (~272)

> `test_apply_dry_run_*` tests that call only `apply(execute=False)` never run the provision branch
> and need no change.

- [ ] **Step 4: Rewrite the three failure tests to drive a `Failed` deployment**

Replace `test_apply_execute_surfaces_captured_stderr_in_step_error` (lines ~328-345) entirely with:

```python
def test_apply_execute_surfaces_failed_operation_reason(tmp_path):
    # The deployment now runs --no-wait; the real failure reason comes from the
    # failed operation's statusMessage, surfaced via DeploymentFailedError.__str__.
    quota = "ErrCode_InsufficientVCPUQuota: remaining 0 for family standardDSv5Family"
    failed_ops = json.dumps([
        {"properties": {
            "provisioningState": "Failed",
            "statusMessage": {"error": {"code": "QuotaExceeded", "message": quota}},
            "targetResource": {
                "resourceType": "Microsoft.App/containerApps",
                "resourceName": "dsf-orchestrator-demo",
            },
        }}
    ])

    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, "{}", state="Failed", ops_json=failed_ops)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="{}")

    spec = InstanceSpec(product="demo", owner="acme", name_prefix="demox123")
    prov = InstanceProvisioner(spec, run=fake_run, repo_root=tmp_path, sleep=lambda *_a: None)
    manifest = prov.apply(execute=True)
    failed = next(s for s in manifest.plan.steps if s.result == "failed")
    assert failed.name == "provision_azure"
    assert quota in failed.error  # the real reason, not just "exit status 1"
```

In `test_apply_execute_records_failure_and_stops_without_raising` (lines ~348-372), replace the
`fake_run` body's `create`-raises branch:

```python
        if cmd[:4] == ["az", "deployment", "group", "create"]:
            raise subprocess.CalledProcessError(1, cmd)
        return MagicMock(returncode=0, stdout="")
```

with:

```python
        failed_ops = json.dumps([{"properties": {
            "provisioningState": "Failed",
            "targetResource": {"resourceType": "Microsoft.App/containerApps",
                               "resourceName": "dsf-orchestrator-demo"},
        }}])
        hit = _az_deploy(cmd, "{}", state="Failed", ops_json=failed_ops)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")
```

and add `sleep=lambda *_a: None` to that test's `InstanceProvisioner(...)` constructor call. The
existing assertions (result `"failed"`, `executed is False`, later steps unrun, persisted prefix)
stay unchanged.

In `test_apply_execute_emits_error_event_on_failure` (lines ~400-419): apply the same `fake_run`
rewrite as above (drive `state="Failed"` via `_az_deploy`), add `sleep=lambda *_a: None` to the
constructor, then change the error-type assertion:

```python
    assert isinstance(err, subprocess.CalledProcessError)
```

to:

```python
    from dsf.instance.deploy_progress import DeploymentFailedError
    assert isinstance(err, DeploymentFailedError)
```

- [ ] **Step 5: Add an `on_progress` forwarding test**

Add this test to `cli/tests/instance/test_provisioner.py` (e.g. after
`test_apply_execute_emits_error_event_on_failure`):

```python
def test_apply_execute_forwards_provision_progress_lines(tmp_path):
    # provision_azure streams per-resource lines through the on_progress channel.
    ops = json.dumps([{"properties": {
        "provisioningState": "Running",
        "targetResource": {"resourceType": "Microsoft.DocumentDB/databaseAccounts",
                           "resourceName": "cosmos-demo"},
    }}])

    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON, ops_json=ops)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="{}")

    lines: list[str] = []
    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    prov.apply(execute=True, on_progress=lines.append)
    assert any("cosmos-demo" in line for line in lines)
```

- [ ] **Step 6: Run the provisioner tests to verify the relevant ones fail**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -q`
Expected: FAIL — `test_apply_execute_forwards_provision_progress_lines` errors on the unknown
`on_progress` kwarg; the migrated/rewritten tests fail because `create` still carries
`--query/-o json` and the provision branch still reads outputs from the `create` call.

- [ ] **Step 7: Change the provision_azure command in `plan()`**

In `cli/src/dsf/instance/provisioner.py`, in `plan()`, replace the tail of the `provision_azure`
step command:

```python
                    f"runtimeImage={s.runtime_image}",
                    "--query", "properties.outputs", "-o", "json",
                ],
```

with:

```python
                    f"runtimeImage={s.runtime_image}",
                    "--no-wait",
                ],
```

- [ ] **Step 8: Import the poller**

Add to the `dsf.instance.*` imports in `cli/src/dsf/instance/provisioner.py` (after the
`from dsf.instance.spec import (...)` block, line ~41):

```python
from dsf.instance.deploy_progress import DeploymentProgressPoller
```

- [ ] **Step 9: Thread `on_progress` through `apply`**

In `apply`, add the parameter and forward it. Change the signature:

```python
    def apply(
        self,
        *,
        execute: bool = False,
        on_event: StepEvent | None = None,
    ) -> InstanceManifest:
```

to:

```python
    def apply(
        self,
        *,
        execute: bool = False,
        on_event: StepEvent | None = None,
        on_progress: Callable[[str], None] | None = None,
    ) -> InstanceManifest:
```

and change the `_execute_step` call inside the loop:

```python
                    azure_result = self._execute_step(
                        step,
                        execute=execute,
                        executed=executed,
                        azure_result=azure_result,
                        plan=plan,
                    )
```

to:

```python
                    azure_result = self._execute_step(
                        step,
                        execute=execute,
                        executed=executed,
                        azure_result=azure_result,
                        plan=plan,
                        on_progress=on_progress,
                    )
```

(Add a sentence to the `apply` docstring: "``on_progress`` receives live per-resource lines while
``provision_azure`` polls its deployment.")

- [ ] **Step 10: Drive the poller in `_execute_step`**

Change the `_execute_step` signature:

```python
    def _execute_step(
        self,
        step: ProvisionStep,
        *,
        execute: bool,
        executed: bool,
        azure_result: AzureProvisionResult | None,
        plan: InstancePlan,
    ) -> AzureProvisionResult | None:
```

to add `on_progress`:

```python
    def _execute_step(
        self,
        step: ProvisionStep,
        *,
        execute: bool,
        executed: bool,
        azure_result: AzureProvisionResult | None,
        plan: InstancePlan,
        on_progress: Callable[[str], None] | None = None,
    ) -> AzureProvisionResult | None:
```

Replace the `provision_azure` branch:

```python
        elif step.name == "provision_azure":
            proc = self._run(step.command, check=True, capture_output=True, text=True)
            azure_result = self._azure_result(proc)
            step.executed, step.result = True, "executed"
```

with:

```python
        elif step.name == "provision_azure":
            # Kick off the deployment asynchronously, then poll its operations so
            # each resource streams to the console; outputs are read post-deploy.
            self._run(step.command, check=True, capture_output=True, text=True)
            poller = DeploymentProgressPoller(
                run=self._run, sleep=self._sleep, emit=on_progress
            )
            outputs = poller.stream(self.spec.resource_group(), self.spec.deployment_name())
            azure_result = self._azure_result_from_outputs(outputs)
            step.executed, step.result = True, "executed"
```

- [ ] **Step 11: Refactor `_azure_result(proc)` → `_azure_result_from_outputs(parsed)`**

Replace the whole `_azure_result` method:

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

with:

```python
    def _azure_result_from_outputs(self, parsed: Any) -> AzureProvisionResult:
        """Map an ``az deployment ... --query properties.outputs`` dict to a result.

        Unwraps each ``{"type": ..., "value": ...}`` envelope to its value; fed by the
        ``provision_azure`` poller's post-deploy ``show`` outputs.
        """
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

- [ ] **Step 12: Run the provisioner tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -q`
Expected: PASS (all tests in the file).

- [ ] **Step 13: Lint**

Run: `uv run ruff check cli/src/dsf/instance/provisioner.py cli/tests/instance/test_provisioner.py`
Expected: `All checks passed!`

- [ ] **Step 14: Commit**

```bash
git add cli/src/dsf/instance/provisioner.py cli/tests/instance/test_provisioner.py
git commit -m "feat(cli): poll provision_azure with --no-wait and stream resource progress"
```

---

## Task 3: Wire `on_progress` from the factory CLI

**Files:**
- Modify: `cli/src/dsf/cli/factory.py`
- Test: `cli/tests/cli/test_factory.py`

- [ ] **Step 1: Widen the existing fake and add the indentation test (will fail)**

In `cli/tests/cli/test_factory.py`, update `_FailingProvisioner.apply` (line ~175) to accept the
new kwarg:

```python
        def apply(self, *, execute=False, on_event=None):
```

becomes:

```python
        def apply(self, *, execute=False, on_event=None, on_progress=None):
```

Then add this test after `test_new_execute_surfaces_step_failure_and_exits_nonzero` (line ~198):

```python
def test_new_execute_indents_provision_progress(capsys, tmp_path, monkeypatch):
    # provision_azure progress lines are indented under the step line.
    from dsf.instance import provisioner as prov_mod
    from dsf.instance.spec import InstanceManifest, InstancePlan, ProvisionStep

    class _ProgressProvisioner:
        def __init__(self, spec, repo_root=None):
            self.spec = spec

        def apply(self, *, execute=False, on_event=None, on_progress=None):
            step = ProvisionStep(
                name="provision_azure", description="deploy backing services", result="executed"
            )
            if on_event is not None:
                on_event("start", 5, 11, step, None)
            if on_progress is not None:
                on_progress(
                    "· Microsoft.App/containerApps dsf-orchestrator-demo: ✓ Succeeded (1m04s)"
                )
            if on_event is not None:
                on_event("done", 5, 11, step, None)
            plan = InstancePlan(product=self.spec.product, steps=[step])
            return InstanceManifest(spec=self.spec, plan=plan, executed=True)

    monkeypatch.setattr(prov_mod, "InstanceProvisioner", _ProgressProvisioner)
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "demopfx", "--config-root", str(tmp_path),
    ])
    out = capsys.readouterr().out
    assert rc == 0
    assert (
        "[dsf]     · Microsoft.App/containerApps dsf-orchestrator-demo: ✓ Succeeded" in out
    )
```

- [ ] **Step 2: Run the factory tests to verify the new test fails**

Run: `uv run pytest cli/tests/cli/test_factory.py -q`
Expected: FAIL — `test_new_execute_indents_provision_progress` does not find the indented line
(the `on_progress` callback is never passed).

- [ ] **Step 3: Add the progress printer and pass it from `_cmd_new`**

In `cli/src/dsf/cli/factory.py`, add after `_print_step_event` (line ~50):

```python
def _print_step_progress(line: str) -> None:
    """Live per-resource progress under an executing step (indented)."""
    print(f"[dsf]     {line}", flush=True)
```

Then in `_cmd_new`, change the execute apply call:

```python
        plan = prov.apply(execute=True, on_event=_print_step_event).plan
```

to:

```python
        plan = prov.apply(
            execute=True, on_event=_print_step_event, on_progress=_print_step_progress
        ).plan
```

- [ ] **Step 4: Run the factory tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_factory.py -q`
Expected: PASS (all tests in the file).

- [ ] **Step 5: Lint**

Run: `uv run ruff check cli/src/dsf/cli/factory.py cli/tests/cli/test_factory.py`
Expected: `All checks passed!`

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/cli/factory.py cli/tests/cli/test_factory.py
git commit -m "feat(cli): stream provision_azure progress under the dsf new step line"
```

---

## Task 4: Docs + full-suite validation

**Files:**
- Modify: `docs/RUNBOOK.md`
- Modify: `infra/README.md`

- [ ] **Step 1: Add the RUNBOOK note**

In `docs/RUNBOOK.md`, immediately after the closing ```` ``` ```` of the `dsf new` example block
(the line after `uv run dsf new --product microbi --owner your-org --name-prefix microbi --execute`)
and before `### The rendered per-product council runtime`, insert:

```markdown

> **Live progress during step 5.** `provision_azure` runs `az deployment group create --no-wait`
> and polls the deployment, streaming each Azure resource as it starts and finishes (indented
> `· <type> <name>: <state>` lines) so a multi-minute deployment is never silent. On failure the
> specific failed resource and its reason are surfaced. Tune the poll cadence with
> `DSF_DEPLOY_POLL_INTERVAL` (seconds, default 5).
```

- [ ] **Step 2: Add the infra/README pointer**

In `infra/README.md`, at the end of the `### Per-product council runtime (rendered by `dsf new`)`
paragraph (just before `## Notes`), append:

```markdown

Step 5 (`provision_azure`) deploys with `--no-wait` and streams per-resource progress to the
console while it polls; set `DSF_DEPLOY_POLL_INTERVAL` (seconds, default 5) to tune the cadence.
```

- [ ] **Step 3: Full gates**

Run: `uv run ruff check . && uv run lint-imports && uv run pytest -q`
Expected: ruff `All checks passed!`; lint-imports `4 kept, 0 broken`; pytest all passed (the prior
baseline of 520 + the new deploy_progress and on_progress tests, minus none removed).

- [ ] **Step 4: Commit**

```bash
git add docs/RUNBOOK.md infra/README.md
git commit -m "docs: note live provision_azure progress and DSF_DEPLOY_POLL_INTERVAL"
```

---

## Self-Review

**Spec coverage:**
- §3 no-wait + poll flow → Task 2 Steps 7, 10 (create `--no-wait`; poller drives list/state/show).
- §4 `DeploymentProgressPoller` (list parsing, per-resource diff, line format, terminal detection,
  duration, skip untargeted) → Task 1. Line format `· <type> <name>: <state> (<dur>)`, `…`/`✓`/`✗`
  symbols, duration omitted when absent → `_format_line` + `_format_duration` + tests.
- §4 `apply(..., on_progress=)` additive channel, `on_event` unchanged → Task 2 Step 9.
- §4 `_execute_step` no-wait + poller; split outputs fetch → Task 2 Steps 10-11.
- §4 `factory._cmd_new` indented `on_progress` alongside unchanged `_print_step_event` (now shared
  with `_cmd_delete`) → Task 3.
- §4 poll interval default 5s + `DSF_DEPLOY_POLL_INTERVAL` → `_resolve_poll_interval` + test.
- §3/§1 failure UX (`DeploymentFailedError` carrying the failed op message; `apply` catches it;
  `_format_step_error` surfaces `str()`) → Task 1 (`_failure_detail`/`_status_message`) + Task 2
  Step 4 rewritten failure tests.
- §5 testing (poller units; provision_azure issues no-wait create + show; on_progress forwarded;
  failure still records `failed` and stops; factory indentation) → Tasks 1-3.
- §7 doc line in RUNBOOK/infra README → Task 4.
- §6 out of scope (no other-step progress, no TTY redraw, dry-run unchanged, delete teardown
  separate) → honored: dry-run still hits `not execute → "dry-run"` before the provision branch;
  no spinner; no delete changes.

**Placeholder scan:** none — every code/step shows full content.

**Type consistency:** `DeploymentProgressPoller(run=, sleep=, emit=, interval=)` constructed
identically in Task 1 tests and Task 2 Step 10 (Step 10 omits `interval`, allowed via the
`interval: float | None = None` default). `stream(resource_group, deployment_name) -> dict`
returns the raw outputs envelope consumed by `_azure_result_from_outputs(parsed)` (Task 2 Steps
10-11). `_az_deploy(cmd, outputs_json, *, state, ops_json)` signature used consistently across Task
2 Steps 2-5. `apply(..., on_progress=None)` / `_execute_step(..., on_progress=None)` /
`_print_step_progress(line)` names match across Tasks 2-3. `DeploymentFailedError` referenced in
Task 1 and Task 2 Step 4 from `dsf.instance.deploy_progress`.

**Test-migration completeness (re-checked against source):** enumerated every
`apply(execute=True)` test in `test_provisioner.py`. All 14 success-path tests are in the Step 3
table and all 3 failure tests in Step 4; none are omitted. Caught and fixed three would-have-hung
tests with no `create` branch (`creates_labels`, `skips_clone…`, `clones…`) → Shape B insert; and
four wrong-output args — the SRE tests and `dry_run_preserves` must pass their **local**
`outputs_json` (not `_AZURE_OUTPUTS_JSON`), and `seed…_fails_when_no_endpoint` passes its inline
`cosmosEndpoint`-only literal. The Hang-hazard callout (Task 2 header + Step 3) explains why an
un-migrated execute test spins forever.
