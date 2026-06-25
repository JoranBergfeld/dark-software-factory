"""Tests for DeploymentProgressPoller (offline, scripted `az` responses)."""

from __future__ import annotations

import json
from unittest.mock import MagicMock

import pytest

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


class _ScriptedAz:
    """Fake `run` returning scripted operation-list / state / outputs payloads.

    ``op_polls[i]`` is the operation-list returned on the i-th poll and
    ``states[i]`` the deployment provisioningState read in that same iteration;
    the last entry is reused if the poller polls more times than scripted.
    """

    def __init__(self, op_polls, states, outputs="{}", error_json="null"):
        self._op_polls = list(op_polls)
        self._states = list(states)
        self._outputs = outputs
        self._error = error_json
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
            if query == "properties.error":
                return MagicMock(returncode=0, stdout=self._error)
        return MagicMock(returncode=0, stdout="")


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
                "resourceName": "dsf-orchestrator",
            },
        }
    }]
    with pytest.raises(DeploymentFailedError) as excinfo:
        _poller(_ScriptedAz([ops], ["Failed"]), lambda _l: None).stream("rg-x", "dep-x")
    message = str(excinfo.value)
    assert quota in message
    assert "dsf-orchestrator" in message


def test_stream_surfaces_deployment_level_error_when_no_failed_operation():
    # Policy/auth denials populate the deployment's properties.error but may leave
    # NO per-operation in a Failed state; that reason must still be surfaced.
    policy = "Resource 'cosmos-x' was disallowed by policy 'deny-public-network'."
    error_json = json.dumps({
        "code": "DeploymentFailed",
        "message": "At least one resource deployment operation failed.",
        "details": [{"code": "RequestDisallowedByPolicy", "message": policy}],
    })
    # The only operation reported is the deployment's own (untargeted) op — no
    # per-resource Failed op carries a statusMessage.
    ops = [{"properties": {"provisioningState": "Failed"}}]
    with pytest.raises(DeploymentFailedError) as excinfo:
        _poller(
            _ScriptedAz([ops], ["Failed"], error_json=error_json), lambda _l: None
        ).stream("rg-x", "dep-x")
    message = str(excinfo.value)
    assert policy in message
    assert "RequestDisallowedByPolicy" in message


def test_stream_tolerates_empty_first_poll():
    res = {"resourceType": "Microsoft.KeyVault/vaults", "resourceName": "kv-x"}
    op_done = [{"properties": {"provisioningState": "Succeeded", "targetResource": res}}]
    lines: list[str] = []
    _poller(_ScriptedAz([[], op_done], ["Running", "Succeeded"]), lines.append).stream(
        "rg-x", "dep-x"
    )
    assert lines == ["· Microsoft.KeyVault/vaults kv-x: ✓ Succeeded"]


def test_stream_raises_on_canceled_terminal_state():
    ops = [{
        "properties": {
            "provisioningState": "Canceled",
            "targetResource": {
                "resourceType": "Microsoft.App/containerApps",
                "resourceName": "dsf-orchestrator",
            },
        }
    }]
    with pytest.raises(DeploymentFailedError) as excinfo:
        _poller(_ScriptedAz([ops], ["Canceled"]), lambda _l: None).stream("rg-x", "dep-x")
    assert "Canceled" in str(excinfo.value)
    assert "dsf-orchestrator" in str(excinfo.value)


def test_stream_tolerates_malformed_outputs_json():
    out = _poller(
        _ScriptedAz([[]], ["Succeeded"], outputs="{not valid json"), lambda _l: None
    ).stream("rg-x", "dep-x")
    assert out == {}


def test_format_duration_renders_iso8601():
    assert _format_duration("PT1M4S") == "1m04s"
    assert _format_duration("PT45S") == "45s"
    assert _format_duration("PT2H3M4S") == "2h03m"
    assert _format_duration(None) == ""
    assert _format_duration("garbage") == ""


def test_status_message_handles_string_envelope_and_unknown():
    assert _status_message("disk quota exceeded") == "disk quota exceeded"
    assert _status_message({"error": {"message": "  boom  "}}) == "boom"
    assert _status_message({"status": "Conflict"}) == "Conflict"
    assert _status_message({"foo": "bar"}) == '{"foo": "bar"}'
    assert _status_message(None) == ""


def test_parse_json_returns_empty_on_bad_or_mistyped_payload():
    assert _parse_json("", dict) == {}
    assert _parse_json("not json", dict) == {}
    assert _parse_json("[1, 2]", dict) == {}  # parses, but wrong type
    assert _parse_json('{"a": 1}', list) == []  # parses, but wrong type
    assert _parse_json('{"a": 1}', dict) == {"a": 1}


def test_resolve_poll_interval_env_override_and_floor(monkeypatch):
    monkeypatch.delenv("DSF_DEPLOY_POLL_INTERVAL", raising=False)
    assert _resolve_poll_interval() == _DEFAULT_POLL_INTERVAL
    monkeypatch.setenv("DSF_DEPLOY_POLL_INTERVAL", "0.2")
    assert _resolve_poll_interval() == 1.0  # floored to 1s
    monkeypatch.setenv("DSF_DEPLOY_POLL_INTERVAL", "12")
    assert _resolve_poll_interval() == 12.0
    monkeypatch.setenv("DSF_DEPLOY_POLL_INTERVAL", "bad")
    assert _resolve_poll_interval() == _DEFAULT_POLL_INTERVAL


def test_stream_times_out_cancels_and_names_wedged_resource():
    conn = {
        "resourceType": "Microsoft.KeyVault/vaults",
        "resourceName": "petkv-demo",
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
    assert "petkv-demo" in msg
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


def test_stream_timeout_disabled_does_not_read_clock():
    def monotonic():
        raise AssertionError("disabled timeout must not read monotonic clock")

    out = _poller(
        _ScriptedAz([[]], ["Succeeded"]),
        lambda _l: None, timeout=0, monotonic=monotonic,
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
