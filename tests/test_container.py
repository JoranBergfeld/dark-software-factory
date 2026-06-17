"""Tests for the service container + CLI skeleton (plan Task 0.4)."""

from __future__ import annotations

import json

import pytest

from dsf.cli import build_parser, main
from dsf.container import Services, build_services
from dsf.fakes import (
    FakeConfigStore,
    FakeGitHubClient,
    FakeMemoryStore,
    FakeModelClient,
    FakeTracer,
)
from dsf.github_client import RealGitHubClient
from dsf.ports import ConfigStore, GitHubClient, MemoryStore, ModelClient, Tracer


def test_build_services_local_wires_fakes():
    services = build_services("local")
    assert isinstance(services, Services)
    assert services.mode == "local"
    assert isinstance(services.model, FakeModelClient)
    assert isinstance(services.memory, FakeMemoryStore)
    assert isinstance(services.config, FakeConfigStore)
    assert isinstance(services.github, FakeGitHubClient)
    assert isinstance(services.tracer, FakeTracer)


def test_build_services_satisfy_protocols():
    services = build_services("local")
    assert isinstance(services.model, ModelClient)
    assert isinstance(services.memory, MemoryStore)
    assert isinstance(services.config, ConfigStore)
    assert isinstance(services.github, GitHubClient)
    assert isinstance(services.tracer, Tracer)


def test_build_services_gh_mode_uses_real_github_client():
    services = build_services("gh")
    assert isinstance(services, Services)
    assert services.mode == "gh"
    assert isinstance(services.github, RealGitHubClient)
    # Satisfies the port protocol.
    assert isinstance(services.github, GitHubClient)


def test_build_services_unknown_mode_raises():
    with pytest.raises(NotImplementedError):
        build_services("azure")


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
    """--mode azure must exit non-zero with a clear message, no traceback."""
    # _get_services calls sys.exit(1) for NotImplementedError; catch SystemExit here.
    with pytest.raises(SystemExit) as exc_info:
        main(["--mode", "azure", "sweep"])
    assert exc_info.value.code == 1
    err = capsys.readouterr().err
    assert "not yet supported" in err
    assert "azure" in err
