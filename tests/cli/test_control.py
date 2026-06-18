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
    for cmd in ("run", "sweep", "serve-agent", "serve-orchestrator", "control-center", "sre-sweep"):
        args = parser.parse_args([cmd])
        assert args.command == cmd


def test_cli_sre_sweep_parses_flags():
    parser = build_parser()
    args = parser.parse_args(["sre-sweep", "--dry-run", "--product", "microbi"])
    assert args.command == "sre-sweep"
    assert args.dry_run is True
    assert args.product == "microbi"


def test_cli_sre_sweep_dispatches(monkeypatch, capsys):
    calls: dict = {}

    async def fake_run_sweep(services, scope=None, *, dry_run=False):
        from dsf.sre.models import SreSweepResult

        calls["scope"] = scope
        calls["dry_run"] = dry_run
        return SreSweepResult(
            observed=6, incidents=5, filed=["local://issue/1"], duplicates=0, dry_run=dry_run
        )

    monkeypatch.setattr("dsf.sre.main.run_sweep", fake_run_sweep)
    rc = main(["sre-sweep", "--dry-run", "--product", "microbi"])
    assert rc == 0
    out = capsys.readouterr().out
    assert "observed=6" in out
    assert "filed=1" in out
    assert calls["dry_run"] is True
    assert calls["scope"] == {"products": ["microbi"]}


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
