"""Tests for the `dsf` factory CLI (`dsf new`)."""

from __future__ import annotations

import pytest

from dsf.cli.factory import build_parser, main
from dsf.instance.spec import read_manifest


def test_new_parser_wiring():
    args = build_parser().parse_args(
        ["new", "--product", "demo", "--owner", "acme", "--name-prefix", "demopfx"]
    )
    assert args.command == "new"
    assert args.product == "demo"
    assert args.owner == "acme"
    assert args.name_prefix == "demopfx"
    assert args.environment == "dev"
    assert args.location == "swedencentral"
    assert args.squad_maturity == "low"
    assert args.execute is False
    assert args.write_plan is False


def test_new_squad_maturity_high_flows_into_manifest(tmp_path):
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "demopfx", "--squad-maturity", "high",
        "--write-plan", "--config-root", str(tmp_path),
    ])
    assert rc == 0
    assert read_manifest("demo", repo_root=tmp_path).spec.squad_maturity == "high"


def test_new_rejects_unknown_squad_maturity():
    with pytest.raises(SystemExit):
        build_parser().parse_args([
            "new", "--product", "demo", "--owner", "acme",
            "--name-prefix", "demopfx", "--squad-maturity", "wild",
        ])


def test_new_requires_name_prefix():
    with pytest.raises(SystemExit):
        build_parser().parse_args(["new", "--product", "demo", "--owner", "acme"])


def test_new_dry_run_prints_plan_without_side_effects(capsys, tmp_path):
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "demopfx", "--config-root", str(tmp_path),
    ])
    out = capsys.readouterr().out
    assert rc == 0
    assert "create_repo" in out
    assert "provision_azure" in out
    assert "register_product" in out
    assert "onboard_sre_agent" in out
    # pure preview: no manifest written even though a config root was provided
    assert not (tmp_path / "config" / "instances" / "demo.json").exists()
    # ...and registration never fires in pure preview (plan() only, no apply)
    assert not (tmp_path / "config" / "products.json").exists()


def test_new_write_plan_writes_manifest(tmp_path):
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "demopfx", "--write-plan", "--config-root", str(tmp_path),
    ])
    assert rc == 0
    assert (tmp_path / "config" / "instances" / "demo.json").exists()


def test_new_effective_prefix_is_stable_across_runs(tmp_path):
    argv = [
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "acmebase", "--write-plan", "--config-root", str(tmp_path),
    ]
    assert main(argv) == 0
    first = read_manifest("demo", repo_root=tmp_path).spec.name_prefix
    assert main(argv) == 0
    second = read_manifest("demo", repo_root=tmp_path).spec.name_prefix
    assert first == second  # reused, not regenerated
    assert first.startswith("acmebase")
    assert len(first) == 12


def test_factory_parser_rejects_runtime_command():
    # the factory CLI must NOT expose runtime ops — those live in dsfctl
    with pytest.raises(SystemExit):
        build_parser().parse_args(["run"])
