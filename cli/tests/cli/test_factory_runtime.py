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


def test_serve_agent_forwards_kind_host_port(monkeypatch):
    calls = _capture_forward(monkeypatch)
    rc = factory.main(
        ["serve-agent", "--kind", "grafana", "--host", "127.0.0.1", "--port", "9000"]
    )
    assert rc == 0
    assert calls[0] == [
        sys.executable,
        "-m",
        "dsf.runtime.control",
        "serve-agent",
        "--kind",
        "grafana",
        "--host",
        "127.0.0.1",
        "--port",
        "9000",
    ]

    rc = factory.main(["serve-agent"])
    assert rc == 0
    assert calls[1] == [
        sys.executable,
        "-m",
        "dsf.runtime.control",
        "serve-agent",
        "--kind",
        "sentry",
        "--host",
        "0.0.0.0",
        "--port",
        "8080",
    ]


def test_omits_optional_flags_when_absent(monkeypatch):
    calls = _capture_forward(monkeypatch)

    assert factory.main(["sweep"]) == 0
    assert factory.main(["run"]) == 0
    assert factory.main(["serve-orchestrator"]) == 0

    assert calls[0] == [sys.executable, "-m", "dsf.runtime.control", "sweep"]
    assert calls[1] == [sys.executable, "-m", "dsf.runtime.control", "run"]
    assert calls[2] == [
        sys.executable,
        "-m",
        "dsf.runtime.control",
        "serve-orchestrator",
    ]
