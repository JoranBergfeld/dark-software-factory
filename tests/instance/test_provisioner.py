"""Tests for InstanceProvisioner.plan() and apply()."""

from __future__ import annotations

from unittest.mock import MagicMock

from dsf.instance.provisioner import InstanceProvisioner
from dsf.instance.spec import InstanceSpec


def _spec() -> InstanceSpec:
    return InstanceSpec(product="demo", owner="acme")


def test_plan_step_order_and_names():
    plan = InstanceProvisioner(_spec()).plan()
    assert plan.product == "demo"
    assert [s.name for s in plan.steps] == [
        "create_repo",
        "squad_init",
        "squad_copilot",
        "provision_azure",
        "deploy_council",
        "deploy_sre",
        "write_config",
    ]


def test_plan_deferred_flags():
    plan = InstanceProvisioner(_spec()).plan()
    deferred = {s.name for s in plan.steps if s.deferred}
    assert deferred == {"provision_azure", "deploy_council", "deploy_sre"}


def test_plan_create_repo_command():
    plan = InstanceProvisioner(_spec()).plan()
    create = next(s for s in plan.steps if s.name == "create_repo")
    assert create.command[:3] == ["gh", "repo", "create"]
    assert "acme/demo" in create.command
    assert "--private" in create.command


def test_plan_squad_steps_run_in_repo_dir():
    plan = InstanceProvisioner(_spec()).plan()
    for name in ("squad_init", "squad_copilot"):
        step = next(s for s in plan.steps if s.name == name)
        assert step.cwd == "demo"
        assert step.command[0] == "squad"


def test_apply_dry_run_writes_manifest_and_runs_nothing(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return MagicMock(returncode=0)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=False)

    assert calls == []  # dry-run shells out to nothing
    assert manifest.executed is False
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["create_repo"] == "dry-run"
    assert results["provision_azure"] == "deferred"
    assert results["write_config"].endswith("demo.json")
    assert (tmp_path / "config" / "instances" / "demo.json").exists()


def test_apply_execute_runs_real_steps_and_stubs_deferred(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append((cmd, kwargs.get("cwd")))
        returncode = 1 if cmd[:3] == ["gh", "repo", "view"] else 0
        return MagicMock(returncode=returncode)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    executed = [cmd for cmd, _ in calls]
    # repo created and squad initialized in the cloned repo dir:
    assert ["gh", "repo", "create", "acme/demo", "--private", "--clone"] in executed
    squad_init = next((cmd, cwd) for cmd, cwd in calls if cmd[:2] == ["squad", "init"])
    assert squad_init[1] == "demo"
    # deferred subsystems are never invoked in SP1:
    assert not any(cmd[0] == "az" for cmd, _ in calls)
    assert manifest.executed is True
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["create_repo"] == "executed"
    assert results["deploy_council"] == "deferred"


def test_apply_execute_is_idempotent_when_repo_exists(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        returncode = 0 if cmd[:3] == ["gh", "repo", "view"] else 0
        return MagicMock(returncode=returncode)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    # repo already exists -> create is skipped:
    assert ["gh", "repo", "create", "acme/demo", "--private", "--clone"] not in calls
    create = next(s for s in manifest.plan.steps if s.name == "create_repo")
    assert create.result == "exists"
    assert create.executed is True
