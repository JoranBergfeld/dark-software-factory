"""Tests for InstanceProvisioner.plan() and apply()."""

from __future__ import annotations

import subprocess
from pathlib import Path
from unittest.mock import MagicMock

import pytest

from dsf.config.registry import load_registry, route_product
from dsf.contracts.handoff import HANDOFF_LABEL, HANDOFF_LABEL_COLOR
from dsf.instance.provisioner import InstanceProvisioner
from dsf.instance.spec import InstanceSpec, read_manifest


def _spec() -> InstanceSpec:
    return InstanceSpec(product="demo", owner="acme")


def test_plan_step_order_and_names():
    plan = InstanceProvisioner(_spec()).plan()
    assert plan.product == "demo"
    assert [s.name for s in plan.steps] == [
        "create_repo",
        "create_labels",
        "squad_init",
        "create_resource_group",
        "provision_azure",
        "register_product",
        "deploy_council",
        "deploy_squad_ralph",
        "squad_governance",
        "onboard_sre_agent",
        "write_config",
    ]


def test_plan_deferred_flags():
    plan = InstanceProvisioner(_spec()).plan()
    deferred = {s.name for s in plan.steps if s.deferred}
    assert deferred == set()


def test_plan_create_resource_group_command():
    plan = InstanceProvisioner(_spec()).plan()
    rg = next(s for s in plan.steps if s.name == "create_resource_group")
    assert rg.command == [
        "az", "group", "create",
        "--name", "rg-dsf-demo", "--location", "swedencentral",
    ]


def test_plan_provision_azure_command_shape():
    plan = InstanceProvisioner(_spec()).plan()
    az = next(s for s in plan.steps if s.name == "provision_azure")
    assert az.command[:4] == ["az", "deployment", "group", "create"]
    assert az.command[az.command.index("-g") + 1] == "rg-dsf-demo"
    assert az.command[az.command.index("-n") + 1] == "dsf-demo"
    assert az.command[az.command.index("-f") + 1].endswith("infra/main.bicep")
    assert "namePrefix=dsf" in az.command
    assert "environmentName=dev" in az.command
    assert "location=swedencentral" in az.command
    assert "product=demo" in az.command
    assert any(c.startswith("runtimeImage=") for c in az.command)
    assert az.command[az.command.index("--query") + 1] == "properties.outputs"
    assert az.command[-2:] == ["-o", "json"]


def test_plan_create_repo_command():
    plan = InstanceProvisioner(_spec()).plan()
    create = next(s for s in plan.steps if s.name == "create_repo")
    assert create.command[:3] == ["gh", "repo", "create"]
    assert "acme/demo" in create.command
    assert "--private" in create.command


def test_plan_squad_steps_run_in_repo_dir():
    plan = InstanceProvisioner(_spec()).plan()
    for name in ("squad_init",):
        step = next(s for s in plan.steps if s.name == name)
        assert step.cwd == "demo"
        assert step.command[0] == "squad"


def test_plan_create_labels_covers_taxonomy_and_handoff():
    spec = _spec()
    plan = InstanceProvisioner(spec).plan()
    labels = next(s for s in plan.steps if s.name == "create_labels")
    # No single command — it emits one gh-label-create per label.
    assert labels.command == []
    created = [c[3] for c in labels.commands]
    # Every taxonomy value is created...
    for group in spec.label_taxonomy.values():
        for name in group:
            assert name in created
    # ...plus the universal handoff label, idempotently (--force), via --repo.
    assert HANDOFF_LABEL in created
    for cmd in labels.commands:
        assert cmd[:3] == ["gh", "label", "create"]
        assert "--force" in cmd
        assert cmd[cmd.index("--repo") + 1] == spec.github_repo()
    handoff_cmd = next(c for c in labels.commands if c[3] == HANDOFF_LABEL)
    assert handoff_cmd[handoff_cmd.index("--color") + 1] == HANDOFF_LABEL_COLOR


def test_apply_execute_creates_labels(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append((cmd, kwargs.get("cwd")))
        returncode = 1 if cmd[:3] == ["gh", "repo", "view"] else 0
        return MagicMock(returncode=returncode)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    executed = [cmd for cmd, _ in calls]
    label_creates = [c for c in executed if c[:3] == ["gh", "label", "create"]]
    assert any(c[3] == HANDOFF_LABEL for c in label_creates)
    assert len(label_creates) >= 1
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["create_labels"] == "executed"


def test_apply_dry_run_records_labels(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return MagicMock(returncode=0)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=False)

    assert calls == []
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["create_labels"] == "dry-run"


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
    assert results["provision_azure"] == "dry-run"
    assert results["deploy_council"] == "rendered (dry-run)"
    assert results["write_config"].endswith("demo.json")
    assert (tmp_path / "config" / "instances" / "demo.json").exists()


def test_apply_execute_registers_product_into_routing_registry(tmp_path):
    def fake_run(cmd, **kwargs):
        returncode = 1 if cmd[:3] == ["gh", "repo", "view"] else 0
        return MagicMock(returncode=returncode)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    products = tmp_path / "config" / "products.json"
    assert products.exists()
    registry = load_registry(products)
    assert registry["demo"].github_repo == "acme/demo"
    # The freshly-registered product is now routable by S6.
    routed = route_product(["demo"], registry)
    assert routed is not None and routed.key == "demo"
    assert {s.name: s.result for s in manifest.plan.steps}["register_product"] == "registered"


def test_apply_dry_run_also_registers_product(tmp_path):
    prov = InstanceProvisioner(_spec(), run=MagicMock(returncode=0), repo_root=tmp_path)
    manifest = prov.apply(execute=False)

    # Registration is a local, idempotent config write — it runs in dry-run too.
    registry = load_registry(tmp_path / "config" / "products.json")
    assert "demo" in registry
    assert registry["demo"].github_repo == "acme/demo"
    assert {s.name: s.result for s in manifest.plan.steps}[
        "register_product"
    ] == "registered (dry-run)"


def test_apply_execute_runs_real_steps_and_onboards_sre(tmp_path):
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
    # azure now provisions for real (RG + Bicep deployment):
    assert [
        "az", "group", "create", "--name", "rg-dsf-demo", "--location", "swedencentral",
    ] in executed
    assert any(cmd[:4] == ["az", "deployment", "group", "create"] for cmd in executed)
    # the council container app is reconciled, but the SRE agent is NOT a
    # Container App — onboarding is wizard/OAuth driven (ADR 0009):
    assert not any(
        cmd[:3] == ["az", "containerapp", "update"]
        and "dsf-sre-demo" in cmd
        for cmd in executed
    )
    assert manifest.executed is True
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["create_repo"] == "executed"
    assert results["deploy_council"] == "deployed"
    assert results["onboard_sre_agent"] == "onboarding ready"


def test_apply_execute_skips_clone_when_repo_and_local_dir_exist(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    (tmp_path / "demo").mkdir()  # local clone already present
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return MagicMock(returncode=0)  # gh repo view -> exists

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    # repo already exists and is cloned -> neither create nor clone runs:
    assert ["gh", "repo", "create", "acme/demo", "--private", "--clone"] not in calls
    assert not any(cmd[:3] == ["gh", "repo", "clone"] for cmd in calls)
    create = next(s for s in manifest.plan.steps if s.name == "create_repo")
    assert create.result == "exists"
    assert create.executed is True


def test_apply_execute_clones_when_repo_exists_but_not_local(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)  # empty cwd: no local clone of the repo
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return MagicMock(returncode=0)  # gh repo view -> exists

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    # repo exists remotely but isn't cloned here -> clone so squad steps have a cwd:
    assert ["gh", "repo", "create", "acme/demo", "--private", "--clone"] not in calls
    assert ["gh", "repo", "clone", "acme/demo", "demo"] in calls
    create = next(s for s in manifest.plan.steps if s.name == "create_repo")
    assert create.result == "cloned"
    assert create.executed is True


def test_apply_execute_captures_azure_outputs(tmp_path):
    outputs_json = (
        '{"cosmosEndpoint": {"type": "String", "value": "https://demo.documents.azure.com"},'
        ' "keyVaultUri": {"type": "String", "value": "https://demovault.vault.azure.net"}}'
    )

    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        if cmd[:4] == ["az", "deployment", "group", "create"]:
            return MagicMock(returncode=0, stdout=outputs_json)
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    assert manifest.azure is not None
    assert manifest.azure.resource_group == "rg-dsf-demo"
    assert manifest.azure.deployment_name == "dsf-demo"
    assert manifest.azure.location == "swedencentral"
    assert manifest.azure.outputs["cosmosEndpoint"] == "https://demo.documents.azure.com"
    assert manifest.azure.outputs["keyVaultUri"] == "https://demovault.vault.azure.net"
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["create_resource_group"] == "executed"
    assert results["provision_azure"] == "executed"


def test_apply_dry_run_leaves_azure_unset(tmp_path):
    def fake_run(cmd, **kwargs):
        return MagicMock(returncode=0)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=False)

    assert manifest.azure is None
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["create_resource_group"] == "dry-run"
    assert results["provision_azure"] == "dry-run"


def test_apply_execute_persists_manifest_even_when_azure_deployment_fails(tmp_path):
    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        if cmd[:4] == ["az", "deployment", "group", "create"]:
            raise subprocess.CalledProcessError(1, cmd)
        return MagicMock(returncode=0, stdout="")

    spec = InstanceSpec(product="demo", owner="acme", name_prefix="demox123")
    prov = InstanceProvisioner(spec, run=fake_run, repo_root=tmp_path)
    with pytest.raises(subprocess.CalledProcessError):
        prov.apply(execute=True)

    # The randomized prefix must survive a failed deployment so a retry reuses the
    # same (globally-unique) resource names instead of orphaning the first attempt.
    manifest = read_manifest("demo", repo_root=tmp_path)
    assert manifest.spec.name_prefix == "demox123"


def test_apply_dry_run_preserves_prior_azure_outputs(tmp_path):
    outputs_json = '{"kv": {"type": "String", "value": "https://v.vault.azure.net"}}'

    def exec_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        if cmd[:4] == ["az", "deployment", "group", "create"]:
            return MagicMock(returncode=0, stdout=outputs_json)
        return MagicMock(returncode=0, stdout="")

    InstanceProvisioner(_spec(), run=exec_run, repo_root=tmp_path).apply(execute=True)

    # A later pure preview / --write-plan must not blank recorded deployment state.
    InstanceProvisioner(_spec(), run=exec_run, repo_root=tmp_path).apply(execute=False)

    manifest = read_manifest("demo", repo_root=tmp_path)
    assert manifest.azure is not None
    assert manifest.azure.outputs["kv"] == "https://v.vault.azure.net"
    assert manifest.executed is True


def test_apply_execute_aca_updates_container_app(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        returncode = 1 if cmd[:3] == ["gh", "repo", "view"] else 0
        return MagicMock(returncode=returncode, stdout="{}")

    spec = InstanceSpec(product="demo", owner="acme")  # runtime_target defaults to aca
    prov = InstanceProvisioner(spec, run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    runtime = tmp_path / "config" / "instances" / "demo.runtime"
    assert (runtime / "containerapp.yaml").is_file()
    assert (runtime / ".env.orchestrator").is_file()
    assert (runtime / "sre-onboarding.md").is_file()
    update = next(
        c for c in calls
        if c[:3] == ["az", "containerapp", "update"]
        and c[c.index("--name") + 1] == "dsf-orchestrator-demo"
    )
    assert "--image" in update
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["deploy_council"] == "deployed"
    assert results["onboard_sre_agent"] == "onboarding ready"


def test_removed_one_shot_squad_steps_are_gone():
    """The pre-Ralph Cloud Agent steps (squad_copilot, squad_triage) are gone."""
    plan = InstanceProvisioner(_spec()).plan()
    names = {s.name for s in plan.steps}
    assert not names & {"squad_copilot", "squad_triage"}


def test_squad_governance_low_maturity_disables_auto_merge():
    spec = InstanceSpec(product="demo", owner="acme", squad_maturity="low")
    plan = InstanceProvisioner(spec).plan()
    gov = next(s for s in plan.steps if s.name == "squad_governance")
    assert gov.commands == [
        ["gh", "api", "--method", "PATCH", "repos/acme/demo", "-F", "allow_auto_merge=false"]
    ]


def test_deploy_squad_ralph_renders_bundle_in_dry_run(tmp_path):
    spec = InstanceSpec(product="demo", owner="acme")
    InstanceProvisioner(spec, repo_root=tmp_path).apply(execute=False)
    squad_dir = tmp_path / "config" / "instances" / "demo.runtime" / "squad"
    assert (squad_dir / "ralph-deployment.yaml").is_file()
    assert (squad_dir / "ralph-scaledobject.yaml").is_file()
    assert (squad_dir / "issue-exporter.yaml").is_file()


def test_deploy_squad_ralph_applies_manifests_on_execute(tmp_path):
    spec = InstanceSpec(product="demo", owner="acme")
    run = MagicMock(return_value=subprocess.CompletedProcess([], 0, stdout="{}", stderr=""))
    InstanceProvisioner(spec, run=run, repo_root=tmp_path).apply(execute=True)
    calls = [c.args[0] for c in run.call_args_list]
    assert any(cmd[:3] == ["az", "aks", "get-credentials"] for cmd in calls)
    applied = {
        Path(cmd[-1]).name
        for cmd in calls
        if cmd[:3] == ["kubectl", "apply", "-f"]
    }
    assert applied == {
        "issue-exporter.yaml",
        "ralph-deployment.yaml",
        "ralph-scaledobject.yaml",
    }
    assert any(
        cmd[:5] == ["gh", "api", "--method", "PATCH", "repos/acme/demo"] for cmd in calls
    )
