"""Tests for the `dsf new` CLI subcommand."""

from __future__ import annotations

from dsf.cli import build_parser, main


def test_new_parser_wiring():
    args = build_parser().parse_args(["new", "--product", "demo", "--owner", "acme"])
    assert args.command == "new"
    assert args.product == "demo"
    assert args.owner == "acme"
    assert args.execute is False
    assert args.write_plan is False


def test_new_dry_run_prints_plan_without_side_effects(capsys, tmp_path):
    rc = main(["new", "--product", "demo", "--owner", "acme"])
    out = capsys.readouterr().out
    assert rc == 0
    assert "create_repo" in out
    assert "squad_init" in out
    assert "deferred" in out
    # pure preview: no manifest written anywhere under tmp_path
    assert not (tmp_path / "config" / "instances" / "demo.json").exists()


def test_new_write_plan_writes_manifest(tmp_path):
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--write-plan", "--config-root", str(tmp_path),
    ])
    assert rc == 0
    assert (tmp_path / "config" / "instances" / "demo.json").exists()
