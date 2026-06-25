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
import time
from collections.abc import Callable
from typing import Any

Runner = Callable[..., Any]

#: Deployment-level provisioning states that end the poll loop.
_TERMINAL_STATES = {"Succeeded", "Failed", "Canceled"}

#: Per-resource states that mark a failed operation (for the failure message).
_FAILED_STATES = {"Failed", "Canceled"}

#: Shown when a failed deployment yields neither a per-op nor a top-level reason.
_NO_FAILURE_DETAIL = "(no failure detail)"

#: Poll cadence default: how often to re-list the deployment's operations.
_DEFAULT_POLL_INTERVAL = 5.0

#: Floor on the poll cadence; guards against a busy-loop (0s) or a negative
#: ``time.sleep`` (ValueError) when a caller passes ``interval`` directly.
_MIN_POLL_INTERVAL = 1.0

#: Wall-clock cap on a single ``stream`` run. ``DSF_DEPLOY_TIMEOUT`` (seconds)
#: overrides it; a value <= 0 disables the bound (wait indefinitely).
_DEFAULT_TIMEOUT = 600.0

#: ISO-8601 duration as Azure emits for an operation (e.g. ``PT1M4S``).
_DURATION_RE = re.compile(r"PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)(?:\.\d+)?S)?$")


class DeploymentFailedError(RuntimeError):
    """An ARM deployment reached a non-``Succeeded`` terminal state.

    ``str()`` names the failed resource operation(s) and their status message so
    the provisioner's ``_format_step_error`` surfaces the real reason.
    """


class DeploymentTimeoutError(RuntimeError):
    """A deployment stayed non-terminal past the poll timeout.

    ``str()`` names the still-running resource operation(s) so the provisioner's
    ``_format_step_error`` surfaces which resource wedged (e.g. a Key Vault or
    Cognitive Services resource whose secret materialization 500s server-side).
    """


def _resolve_poll_interval() -> float:
    """Poll cadence in seconds: ``DSF_DEPLOY_POLL_INTERVAL`` env > 5s, min 1s."""
    raw = os.environ.get("DSF_DEPLOY_POLL_INTERVAL", "").strip()
    if raw:
        try:
            return max(_MIN_POLL_INTERVAL, float(raw))
        except ValueError:
            pass
    return _DEFAULT_POLL_INTERVAL


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


def _format_arm_error(error: Any) -> str:
    """Render a deployment's ``properties.error`` ARM object as a readable reason.

    Prefers the nested ``details[].message`` (where policy/auth denials put the real
    reason); falls back to the top-level ``message``. Returns ``""`` when absent.
    """
    if not isinstance(error, dict) or not error:
        return ""
    messages: list[str] = []
    details = error.get("details")
    if isinstance(details, list):
        for item in details:
            if not isinstance(item, dict):
                continue
            message = item.get("message")
            if isinstance(message, str) and message.strip():
                code = (item.get("code") or "").strip()
                messages.append(f"{code}: {message.strip()}" if code else message.strip())
    if messages:
        return "; ".join(messages)
    top = error.get("message")
    if isinstance(top, str) and top.strip():
        code = (error.get("code") or "").strip()
        return f"{code}: {top.strip()}" if code else top.strip()
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
        ``DSF_DEPLOY_POLL_INTERVAL`` (default 5s) when ``None``. An explicit
        value is floored at ``_MIN_POLL_INTERVAL`` (1s).
    """

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
        start = self._monotonic() if self._timeout > 0 else 0.0
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

    def _failure_summary(self, rg: str, name: str, ops: list[dict[str, Any]]) -> str:
        """Combine per-operation failures with the deployment-level ``properties.error``.

        Per-resource ops carry the most specific reason, but deployment-level failures
        (policy/auth denials, evaluation errors) leave no failed op — their reason lives
        only in ``properties.error``, which the old blocking create surfaced via stderr.
        Fetch both so neither class of failure is reported opaquely.
        """
        parts: list[str] = []
        op_detail = self._failure_detail(ops)
        if op_detail:
            parts.append(op_detail)
        deployment_error = self._deployment_error(rg, name)
        if deployment_error and deployment_error not in op_detail:
            parts.append(deployment_error)
        return "; ".join(parts) if parts else _NO_FAILURE_DETAIL

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
        return "; ".join(failures)

    def _deployment_error(self, rg: str, name: str) -> str:
        """The deployment's top-level ``properties.error`` reason, or ``""``.

        Runs on the failure path only; never raises — a failed error-fetch must not
        mask the underlying deployment failure.
        """
        try:
            proc = self._run(
                [
                    "az", "deployment", "group", "show",
                    "-g", rg, "-n", name,
                    "--query", "properties.error", "-o", "json",
                ],
                check=False, capture_output=True, text=True,
            )
        except Exception:  # noqa: BLE001 - best-effort reason fetch on the failure path
            return ""
        return _format_arm_error(_parse_json(getattr(proc, "stdout", ""), dict))


def _parse_json(raw: Any, expected: type) -> Any:
    """``json.loads`` ``raw`` when it parses to ``expected``; else an empty one."""
    text = raw or ""
    try:
        parsed = json.loads(text) if isinstance(text, str) and text.strip() else expected()
    except json.JSONDecodeError:
        parsed = expected()
    return parsed if isinstance(parsed, expected) else expected()
