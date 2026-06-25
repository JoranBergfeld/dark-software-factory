"""Tests for InstanceProvisioner.plan() and apply()."""

from __future__ import annotations

import json
import re
import subprocess
from unittest.mock import MagicMock

from dsf.config.registry import load_registry, route_product
from dsf.contracts.handoff import HANDOFF_LABEL, HANDOFF_LABEL_COLOR, INCIDENT_LABEL
from dsf.instance.provisioner import (
    _SEED_RETRY_DELAY,
    InstanceProvisioner,
    _appconfig_seed_commands,
)
from dsf.instance.spec import InstanceSpec, _default_repo_root, read_manifest


def _spec() -> InstanceSpec:
    return InstanceSpec(product="demo", owner="acme")


def _completed(stdout="", returncode=0):
    return subprocess.CompletedProcess(args=[], returncode=returncode, stdout=stdout, stderr="")


#: The bicep emits ``appConfigEndpoint``; the ``seed_appconfig`` step reads it to
#: seed config/defaults.json into App Configuration. Execute-path tests must surface
#: it in the provision_azure outputs or the seed step (correctly) fails for want of
#: an endpoint. It also emits ``keyVaultName`` for the optional App-key seed.
_APPCONFIG_ENDPOINT = "https://demo.azconfig.io"
_APPCONFIG_OUTPUT = (
    f'"appConfigEndpoint": {{"type": "String", "value": "{_APPCONFIG_ENDPOINT}"}}'
)
_KEYVAULT_OUTPUT = '"keyVaultName": {"type": "String", "value": "kv-demo-xyz"}'
_AZURE_OUTPUTS_JSON = "{" + _APPCONFIG_OUTPUT + "," + _KEYVAULT_OUTPUT + "}"


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


def _azure_result_with(tmp_path, **outputs):
    from dsf.instance.spec import AzureProvisionResult

    return AzureProvisionResult(
        resource_group="rg-dsf-demo", deployment_name="dsf-demo",
        location="swedencentral", outputs={k: str(v) for k, v in outputs.items()},
    )


def test_plan_step_order_and_names():
    plan = InstanceProvisioner(_spec()).plan()
    assert plan.product == "demo"
    assert [s.name for s in plan.steps] == [
        "create_repo",
        "seed_repo",
        "create_labels",
        "install_app",
        "create_resource_group",
        "provision_azure",
        "seed_appconfig",
        "seed_app_key",
        "register_product",
        "deploy_council",
        "branch_protection",
        "deploy_sre_agent",
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
        "--tags",
        "project=dark-software-factory",
        "managed-by=dsf",
        "product=demo",
        "component=backing-services",
    ]


def test_plan_provision_azure_command_shape():
    plan = InstanceProvisioner(_spec()).plan()
    az = next(s for s in plan.steps if s.name == "provision_azure")
    assert az.command[:4] == ["az", "deployment", "group", "create"]
    assert az.command[az.command.index("-g") + 1] == "rg-dsf-demo"
    assert az.command[az.command.index("-n") + 1] == "dsf-demo"
    bicep_arg = az.command[az.command.index("-f") + 1].replace("\\", "/")
    assert bicep_arg.endswith("infra/main.bicep")
    assert "namePrefix=dsf" in az.command
    assert "environmentName=dev" in az.command
    assert "location=swedencentral" in az.command
    assert "product=demo" in az.command
    assert any(c.startswith("runtimeImage=") for c in az.command)
    assert "--no-wait" in az.command
    assert "--query" not in az.command  # outputs are fetched post-deploy via `show`


def test_provision_azure_passes_github_repository_param():
    spec = _spec()
    plan = InstanceProvisioner(spec).plan()
    step = next(s for s in plan.steps if s.name == "provision_azure")
    assert f"githubRepository={spec.github_repo()}" in step.command


def test_provision_azure_enables_bing_grounding_by_default():
    plan = InstanceProvisioner(_spec()).plan()
    step = next(s for s in plan.steps if s.name == "provision_azure")
    assert "enableBingGrounding=true" in step.command


def test_provision_azure_disables_bing_grounding_when_spec_opts_out():
    spec = InstanceSpec(product="demo", owner="acme", enable_bing_grounding=False)
    plan = InstanceProvisioner(spec).plan()
    step = next(s for s in plan.steps if s.name == "provision_azure")
    assert "enableBingGrounding=false" in step.command
    assert "enableBingGrounding=true" not in step.command


def test_plan_create_repo_command():
    plan = InstanceProvisioner(_spec()).plan()
    create = next(s for s in plan.steps if s.name == "create_repo")
    assert create.command[:3] == ["gh", "repo", "create"]
    assert "acme/demo" in create.command
    assert "--private" in create.command


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
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
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
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0)

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
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    executed = [cmd for cmd, _ in calls]
    # repo created (and cloned locally) for the product:
    assert ["gh", "repo", "create", "acme/demo", "--private", "--clone"] in executed
    # azure now provisions for real (RG + Bicep deployment), tagged managed-by=dsf:
    rg_create = next(c for c in executed if c[:3] == ["az", "group", "create"])
    assert rg_create[:5] == ["az", "group", "create", "--name", "rg-dsf-demo"]
    assert "--location" in rg_create and "swedencentral" in rg_create
    assert "managed-by=dsf" in rg_create
    assert "product=demo" in rg_create
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
    assert "deploy_sre_agent" in results


def test_apply_execute_skips_clone_when_repo_and_local_dir_exist(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    (tmp_path / "demo").mkdir()  # local clone already present
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
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
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0)  # gh repo view -> exists

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    # repo exists remotely but isn't cloned here -> clone so we have a local checkout:
    assert ["gh", "repo", "create", "acme/demo", "--private", "--clone"] not in calls
    assert ["gh", "repo", "clone", "acme/demo", "demo"] in calls
    create = next(s for s in manifest.plan.steps if s.name == "create_repo")
    assert create.result == "cloned"
    assert create.executed is True


def test_apply_execute_captures_azure_outputs(tmp_path):
    outputs_json = (
        "{" + _APPCONFIG_OUTPUT + ","
        ' "cosmosEndpoint": {"type": "String", "value": "https://demo.documents.azure.com"},'
        ' "keyVaultUri": {"type": "String", "value": "https://demovault.vault.azure.net"}}'
    )

    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, outputs_json)
        if hit is not None:
            return hit
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


def test_apply_execute_records_failure_and_stops_without_raising(tmp_path):
    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        failed_ops = json.dumps([{"properties": {
            "provisioningState": "Failed",
            "targetResource": {"resourceType": "Microsoft.App/containerApps",
                               "resourceName": "dsf-orchestrator-demo"},
        }}])
        hit = _az_deploy(cmd, "{}", state="Failed", ops_json=failed_ops)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")

    spec = InstanceSpec(product="demo", owner="acme", name_prefix="demox123")
    prov = InstanceProvisioner(spec, run=fake_run, repo_root=tmp_path, sleep=lambda *_a: None)

    # apply records the failure on the step and STOPS — it does not raise.
    manifest = prov.apply(execute=True)
    steps = {s.name: s for s in manifest.plan.steps}
    assert steps["provision_azure"].result == "failed"
    assert steps["provision_azure"].error  # carries the error message
    assert steps["provision_azure"].executed is False
    # Steps after the failure are left unrun.
    assert steps["deploy_council"].result == ""
    assert steps["deploy_sre_agent"].result == ""

    # The randomized prefix must survive a failed deployment so a retry reuses the
    # same (globally-unique) resource names instead of orphaning the first attempt.
    persisted = read_manifest("demo", repo_root=tmp_path)
    assert persisted.spec.name_prefix == "demox123"


def test_apply_execute_emits_start_and_done_events_per_step(tmp_path):
    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="{}")

    events: list[tuple] = []
    spec = InstanceSpec(product="demo", owner="acme", name_prefix="demox123")
    prov = InstanceProvisioner(spec, run=fake_run, repo_root=tmp_path)
    prov.apply(execute=True, on_event=lambda *a: events.append(a))

    phases = [(phase, step.name) for phase, _i, _t, step, _err in events]
    assert ("start", "create_repo") in phases
    assert ("done", "create_repo") in phases
    assert ("start", "deploy_sre_agent") in phases
    assert ("done", "deploy_sre_agent") in phases
    assert not any(phase == "error" for phase, *_ in events)
    # 1-based index, stable total = the 12 non-write_config steps.
    starts = [e for e in events if e[0] == "start"]
    assert starts[0][1] == 1
    assert all(total == 12 for _p, _i, total, _s, _e in starts)


def test_apply_execute_emits_error_event_on_failure(tmp_path):
    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        failed_ops = json.dumps([{"properties": {
            "provisioningState": "Failed",
            "targetResource": {"resourceType": "Microsoft.App/containerApps",
                               "resourceName": "dsf-orchestrator-demo"},
        }}])
        hit = _az_deploy(cmd, "{}", state="Failed", ops_json=failed_ops)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")

    events: list[tuple] = []
    spec = InstanceSpec(product="demo", owner="acme", name_prefix="demox123")
    prov = InstanceProvisioner(spec, run=fake_run, repo_root=tmp_path, sleep=lambda *_a: None)
    prov.apply(execute=True, on_event=lambda *a: events.append(a))

    errors = [e for e in events if e[0] == "error"]
    assert len(errors) == 1
    phase, _index, _total, step, err = errors[0]
    assert step.name == "provision_azure"
    from dsf.instance.deploy_progress import DeploymentFailedError
    assert isinstance(err, DeploymentFailedError)
    # No "done" emitted for the failed step, and no events after it.
    assert ("done", "provision_azure") not in [(p, s.name) for p, _i, _t, s, _e in events]


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


def test_apply_dry_run_preserves_prior_azure_outputs(tmp_path):
    outputs_json = (
        "{" + _APPCONFIG_OUTPUT + ","
        ' "kv": {"type": "String", "value": "https://v.vault.azure.net"}}'
    )

    def exec_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, outputs_json)
        if hit is not None:
            return hit
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
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1, stdout="{}")
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="{}")

    spec = InstanceSpec(product="demo", owner="acme")  # runtime_target defaults to aca
    prov = InstanceProvisioner(spec, run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    runtime = tmp_path / "config" / "instances" / "demo.runtime"
    assert (runtime / "containerapp.yaml").is_file()
    assert (runtime / ".env.orchestrator").is_file()
    assert (runtime / "sre-agent.md").is_file()
    update = next(
        c for c in calls
        if c[:3] == ["az", "containerapp", "update"]
        and c[c.index("--name") + 1] == "dsf-orchestrator-demo"
    )
    assert "--image" in update
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["deploy_council"] == "deployed"
    assert "deploy_sre_agent" in results


def test_removed_one_shot_squad_steps_are_gone():
    """The retired squad steps (Cloud Agent + AKS/Ralph harness) are gone."""
    plan = InstanceProvisioner(_spec()).plan()
    names = {s.name for s in plan.steps}
    assert not names & {"squad_copilot", "squad_triage", "deploy_squad_ralph", "squad_init"}


def test_branch_protection_step_present_and_has_no_static_commands():
    spec = InstanceSpec(product="demo", owner="acme", creation_maturity="low")
    plan = InstanceProvisioner(spec).plan()
    step = next(s for s in plan.steps if s.name == "branch_protection")
    assert step.commands == []
    assert step.command == []


def test_branch_protection_dry_run_records_plan(tmp_path):
    spec = InstanceSpec(product="demo", owner="acme", creation_maturity="high")
    manifest = InstanceProvisioner(spec, repo_root=tmp_path).apply(execute=False)
    step = next(s for s in manifest.plan.steps if s.name == "branch_protection")
    assert step.result == "ruleset planned (dry-run)"
    assert step.executed is False


def test_branch_protection_execute_creates_ruleset_and_sets_auto_merge():
    calls: list[dict] = []

    def fake_run(cmd, **kwargs):
        calls.append({"cmd": cmd, "input": kwargs.get("input")})
        if "--jq" in cmd:
            return _completed(stdout="")
        return _completed(stdout="")

    provisioner = InstanceProvisioner(
        InstanceSpec(product="demo", owner="acme", creation_maturity="high"),
        run=fake_run,
    )
    provisioner._apply_branch_protection()

    api_calls = [c for c in calls if c["cmd"][:2] == ["gh", "api"]]
    assert api_calls[0]["cmd"][2] == "/repos/acme/demo/rulesets?includes_parents=false"
    assert api_calls[1]["cmd"][:5] == [
        "gh", "api", "--method", "POST", "/repos/acme/demo/rulesets"
    ]
    assert api_calls[1]["cmd"][-2:] == ["--input", "-"]
    assert json.loads(api_calls[1]["input"])["name"] == "dsf-creation"
    assert api_calls[2]["cmd"] == [
        "gh", "api", "--method", "PATCH", "repos/acme/demo",
        "-F", "allow_auto_merge=true",
    ]


def test_branch_protection_execute_updates_existing_ruleset():
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if "--jq" in cmd:
            return _completed(stdout="123\n")
        return _completed(stdout="")

    provisioner = InstanceProvisioner(
        InstanceSpec(product="demo", owner="acme", creation_maturity="low"),
        run=fake_run,
    )
    provisioner._apply_branch_protection()

    methods = [c for c in calls if c[:2] == ["gh", "api"] and "--method" in c]
    assert methods[0][:5] == ["gh", "api", "--method", "PUT", "/repos/acme/demo/rulesets/123"]


def test_plan_create_labels_includes_incident_marker():
    spec = _spec()
    plan = InstanceProvisioner(spec).plan()
    labels = next(s for s in plan.steps if s.name == "create_labels")
    created = [c[3] for c in labels.commands]
    assert INCIDENT_LABEL in created
    incident_cmd = next(c for c in labels.commands if c[3] == INCIDENT_LABEL)
    assert incident_cmd[:3] == ["gh", "label", "create"]
    assert "--force" in incident_cmd
    assert incident_cmd[incident_cmd.index("--repo") + 1] == spec.github_repo()


def test_deploy_sre_agent_dry_run_records_plan(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return MagicMock(returncode=0)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=False)

    # dry-run must shell out to nothing
    assert calls == []
    step = next(s for s in manifest.plan.steps if s.name == "deploy_sre_agent")
    assert "dry-run" in step.result
    assert step.executed is False


def test_deploy_sre_agent_executes_sub_deployment(tmp_path):
    outputs_json = (
        "{" + _APPCONFIG_OUTPUT + ","
        ' "appInsightsId": {"type": "String", "value": "/sub/ai"},'
        ' "logAnalyticsId": {"type": "String", "value": "/sub/law"},'
        ' "keyVaultName": {"type": "String", "value": "kv1"}}'
    )
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, outputs_json)
        if hit is not None:
            return hit
        if cmd == ["gh", "auth", "token"]:
            return MagicMock(returncode=0, stdout="")
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    prov.apply(execute=True)

    sub = next(c for c in calls if c[:4] == ["az", "deployment", "sub", "create"])
    joined = " ".join(sub)
    assert "infra/sre-agent.bicep" in joined
    assert f"agentName={_spec().sre_agent_name()}" in sub
    assert f"sreAgentLocation={_spec().sre_agent_location}" in sub
    assert any("targetResourceGroups=" in part for part in sub)
    assert "appInsightsId=/sub/ai" in sub
    assert "logAnalyticsId=/sub/law" in sub


def test_deploy_sre_agent_connect_repo_skipped_when_no_endpoint(tmp_path):
    outputs_json = (
        "{" + _APPCONFIG_OUTPUT + ","
        ' "appInsightsId": {"type": "String", "value": "/sub/ai"},'
        ' "logAnalyticsId": {"type": "String", "value": "/sub/law"},'
        ' "keyVaultName": {"type": "String", "value": "kv1"}}'
    )
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, outputs_json)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    # No agentEndpoint in outputs -> repo connect is skipped cleanly
    step = next(s for s in manifest.plan.steps if s.name == "deploy_sre_agent")
    assert "skipped" in step.result
    # az rest must not have been called
    assert not any(cmd[:2] == ["az", "rest"] for cmd in calls)


def test_deploy_sre_agent_connect_repo_skipped_when_no_gh_token(tmp_path):
    outputs_json = (
        "{" + _APPCONFIG_OUTPUT + ","
        ' "appInsightsId": {"type": "String", "value": "/sub/ai"},'
        ' "logAnalyticsId": {"type": "String", "value": "/sub/law"},'
        ' "agentEndpoint": {"type": "String", "value": "https://sre.example.com"},'
        ' "keyVaultName": {"type": "String", "value": "kv1"}}'
    )
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, outputs_json)
        if hit is not None:
            return hit
        if cmd == ["gh", "auth", "token"]:
            return MagicMock(returncode=0, stdout="")  # empty token -> skip
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    step = next(s for s in manifest.plan.steps if s.name == "deploy_sre_agent")
    assert "skipped" in step.result
    assert not any(cmd[:2] == ["az", "rest"] for cmd in calls)


def test_deploy_sre_agent_connect_repo_calls_az_rest_when_token_present(tmp_path):
    outputs_json = (
        "{" + _APPCONFIG_OUTPUT + ","
        ' "appInsightsId": {"type": "String", "value": "/sub/ai"},'
        ' "logAnalyticsId": {"type": "String", "value": "/sub/law"},'
        ' "agentEndpoint": {"type": "String", "value": "https://sre.example.com"},'
        ' "keyVaultName": {"type": "String", "value": "kv1"}}'
    )
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, outputs_json)
        if hit is not None:
            return hit
        if cmd == ["gh", "auth", "token"]:
            return MagicMock(returncode=0, stdout="ghp_fake_token")
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    step = next(s for s in manifest.plan.steps if s.name == "deploy_sre_agent")
    assert step.result == "deployed; repo connected"
    rest_calls = [cmd for cmd in calls if cmd[:2] == ["az", "rest"]]
    assert len(rest_calls) == 1
    rest_cmd = rest_calls[0]
    assert "--method" in rest_cmd and "post" in rest_cmd
    assert any("https://sre.example.com/repositories" in part for part in rest_cmd)


def test_appconfig_seed_commands_flatten_defaults():
    cmds = _appconfig_seed_commands("https://x.azconfig.io")
    # Every command writes one key under the deployer's AAD identity, no label
    # (the unlabelled baseline that per-product labels override), idempotently.
    for cmd in cmds:
        assert cmd[:4] == ["az", "appconfig", "kv", "set"]
        assert cmd[cmd.index("--endpoint") + 1] == "https://x.azconfig.io"
        assert cmd[cmd.index("--auth-mode") + 1] == "login"
        assert "--label" not in cmd
        assert "--yes" in cmd
    pairs = {cmd[cmd.index("--key") + 1]: cmd[cmd.index("--value") + 1] for cmd in cmds}
    # The flags an empty App Config would otherwise read as disabled:
    assert pairs["critics.security.enabled"] == "true"
    assert pairs["agents.SENTRY.enabled"] == "true"
    # Pause flags + scalar defaults, JSON-encoded so the store can json.loads them:
    assert pairs["triggers.SCHEDULED.paused"] == "false"
    assert pairs["default_threshold"] == "0.6"
    assert pairs["default_maturity"] == '"supervised"'
    # A list leaf (jury roster) is seeded whole, not exploded into keys:
    assert pairs["jury.roster"] == '["pragmatist", "skeptic", "user_advocate"]'


def test_seed_appconfig_seeds_from_deployment_endpoint_on_execute(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    seed = next(s for s in manifest.plan.steps if s.name == "seed_appconfig")
    assert seed.result == "seeded"
    assert seed.executed is True
    kv_sets = [c for c in calls if c[:4] == ["az", "appconfig", "kv", "set"]]
    assert kv_sets, "the defaults must be seeded into App Configuration"
    # The endpoint is threaded from the provision_azure outputs, not hardcoded.
    assert all(c[c.index("--endpoint") + 1] == _APPCONFIG_ENDPOINT for c in kv_sets)
    keys = {c[c.index("--key") + 1] for c in kv_sets}
    assert {"critics.grounding.enabled", "agents.WEBIQ.enabled"} <= keys


def test_seed_appconfig_dry_run_records_without_calls(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return MagicMock(returncode=0)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=False)

    seed = next(s for s in manifest.plan.steps if s.name == "seed_appconfig")
    assert seed.result == "seeded (dry-run)"
    assert seed.executed is False
    assert not any(c[:4] == ["az", "appconfig", "kv", "set"] for c in calls)


def test_seed_appconfig_retries_until_rbac_propagates(tmp_path):
    sleeps: list[float] = []
    state = {"denials": 2}  # the Data Owner grant lands after two Forbidden replies

    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        if cmd[:4] == ["az", "appconfig", "kv", "set"]:
            if state["denials"] > 0:
                state["denials"] -= 1
                raise subprocess.CalledProcessError(1, cmd, stderr="ERROR: ... Forbidden")
            return MagicMock(returncode=0, stdout="")
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(
        _spec(), run=fake_run, repo_root=tmp_path, sleep=sleeps.append
    )
    manifest = prov.apply(execute=True)

    seed = next(s for s in manifest.plan.steps if s.name == "seed_appconfig")
    assert seed.result == "seeded"
    assert seed.executed is True
    # Two Forbidden denials -> two backoff sleeps before the batch broke through.
    assert sleeps == [_SEED_RETRY_DELAY, _SEED_RETRY_DELAY]


def test_seed_appconfig_fails_when_no_endpoint_in_outputs(tmp_path):
    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, '{"cosmosEndpoint": {"type": "String", "value": "x"}}')
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    steps = {s.name: s for s in manifest.plan.steps}
    assert steps["seed_appconfig"].result == "failed"
    assert "appConfigEndpoint" in steps["seed_appconfig"].error
    # The line stops at the failed step — later steps are left unrun.
    assert steps["deploy_council"].result == ""


def test_seed_app_key_copies_owner_pem_into_product_vault(tmp_path):
    spec = InstanceSpec(product="demo", owner="acme")
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)

        class R:
            returncode = 0
            stdout = "555\n" if "/repos/" in " ".join(cmd) else "-----PEM-----\n"

        return R()

    prov = InstanceProvisioner(
        spec, run=fake_run, repo_root=tmp_path, sleep=lambda _s: None,
        owner_keyvault_uri="https://kv-dsf-app.vault.azure.net/",
        github_app_id="42", github_installation_id="9001",
    )
    prov._seed_app_key(_azure_result_with(tmp_path, keyVaultName="kv-demo-xyz"))

    show = next(c for c in calls if c[:4] == ["az", "keyvault", "secret", "show"])
    assert "kv-dsf-app" in show  # read from owner vault
    setc = next(c for c in calls if c[:4] == ["az", "keyvault", "secret", "set"])
    assert "kv-demo-xyz" in setc and "--file" in setc  # written to product vault from a file


def test_seed_app_key_raises_without_owner_keyvault(tmp_path):
    import pytest

    prov = InstanceProvisioner(InstanceSpec(product="demo", owner="acme"), repo_root=tmp_path)
    with pytest.raises(RuntimeError, match="owner Key Vault"):
        prov._seed_app_key(_azure_result_with(tmp_path, keyVaultName="kv-demo-xyz"))


def test_provision_azure_passes_github_app_params(tmp_path):
    prov = InstanceProvisioner(
        InstanceSpec(product="demo", owner="acme"),
        repo_root=tmp_path, github_app_id="42", github_installation_id="9001",
    )
    azure_step = next(s for s in prov.plan().steps if s.name == "provision_azure")
    joined = " ".join(azure_step.command)
    assert "githubAppId=42" in joined
    assert "githubInstallationId=9001" in joined


def test_seed_app_key_step_skips_when_no_owner_app_configured(tmp_path):
    # Backward-compat: dsf new without an owner App must still complete; seed_app_key
    # skips gracefully instead of raising and failing the line.
    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)
    seed = next(s for s in manifest.plan.steps if s.name == "seed_app_key")
    assert "skip" in seed.result.lower()
    # the line did NOT stop at seed_app_key — later steps still ran
    assert next(s for s in manifest.plan.steps if s.name == "deploy_sre_agent").executed is True


def test_install_app_adds_repo_to_owner_installation_and_records_binding(tmp_path):
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        if cmd[:3] == ["gh", "api", "/repos/acme/demo"]:
            return MagicMock(returncode=0, stdout="555\n")
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(
        _spec(),
        run=fake_run,
        repo_root=tmp_path,
        owner_keyvault_uri="https://kv-dsf-app.vault.azure.net/",
        github_app_id="42",
        github_installation_id="9001",
    )
    manifest = prov.apply(execute=True)

    # the repo id was looked up, then a PUT added it to the single owner installation
    put = next(c for c in calls if c[:3] == ["gh", "api", "--method"])
    assert "PUT" in put
    assert "/user/installations/9001/repositories/555" in " ".join(put)
    assert manifest.github_app is not None
    assert manifest.github_app.app_id == "42"
    assert manifest.github_app.installation_id == "9001"
    assert manifest.github_app.repository_id == 555


def test_install_app_step_skips_when_no_owner_app_configured(tmp_path):
    # Backward-compat: dsf new without an owner App must still run; install_app
    # skips gracefully rather than issuing a malformed /user/installations//... call.
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    assert not any("/user/installations/" in " ".join(c) for c in calls)  # no install attempt
    assert manifest.github_app is None
    install = next(s for s in manifest.plan.steps if s.name == "install_app")
    assert "skip" in install.result.lower()


def test_install_app_step_is_dry_run_safe(tmp_path):
    prov = InstanceProvisioner(_spec(), repo_root=tmp_path, github_installation_id="9001")
    plan = prov.plan()
    install = next(s for s in plan.steps if s.name == "install_app")
    assert "9001" in install.description


def test_install_app_dry_run_preview_consistent_when_owner_kv_set(tmp_path):
    # In dry-run, factory.py does not read the owner-KV pointers, so installation_id is
    # empty even though an owner App IS configured (owner_keyvault_uri is set). The two App
    # steps must preview consistently: install_app must not claim "no owner App configured"
    # while seed_app_key previews a seed.
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return MagicMock(returncode=0)

    prov = InstanceProvisioner(
        _spec(), run=fake_run, repo_root=tmp_path,
        owner_keyvault_uri="https://kv-dsf-app.vault.azure.net/",
    )
    manifest = prov.apply(execute=False)

    assert calls == []  # dry-run shells out to nothing
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["install_app"] == "installed (dry-run)"
    assert results["seed_app_key"] == "seeded (dry-run)"


def test_apply_preserves_prior_github_app_binding_when_install_skips(tmp_path):
    # A binding recorded by an earlier App install must survive a later run that
    # skips install_app (preview / --write-plan / execute retry without the pointer),
    # mirroring how prior Azure outputs are carried forward.
    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        if cmd[:3] == ["gh", "api", "/repos/acme/demo"]:
            return MagicMock(returncode=0, stdout="555\n")
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")

    InstanceProvisioner(
        _spec(), run=fake_run, repo_root=tmp_path,
        owner_keyvault_uri="https://kv-dsf-app.vault.azure.net/",
        github_app_id="42", github_installation_id="9001",
    ).apply(execute=True)

    # Re-run WITHOUT the owner pointer: install_app skips, but the binding must persist.
    manifest = InstanceProvisioner(_spec(), repo_root=tmp_path).apply(execute=False)
    assert manifest.github_app is not None
    assert manifest.github_app.installation_id == "9001"
    on_disk = read_manifest("demo", repo_root=tmp_path)
    assert on_disk.github_app is not None
    assert on_disk.github_app.app_id == "42"
    assert on_disk.github_app.repository_id == 555


def test_apply_execute_skips_branch_protection_when_plan_unsupported(tmp_path, monkeypatch):
    """A private repo on a Free plan -> rulesets 403; the step is skipped, not failed."""
    monkeypatch.chdir(tmp_path)
    from dsf.instance.branch_protection import RULESET_UNSUPPORTED_RESULT

    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        if len(cmd) > 2 and "rulesets?includes_parents" in str(cmd[2]):
            raise subprocess.CalledProcessError(
                returncode=1,
                cmd=cmd,
                stderr=(
                    "gh: Upgrade to GitHub Pro or make this repository public to "
                    "enable this feature. (HTTP 403)"
                ),
            )
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    results = {s.name: s.result for s in manifest.plan.steps}
    bp = next(s for s in manifest.plan.steps if s.name == "branch_protection")
    assert bp.result == RULESET_UNSUPPORTED_RESULT
    assert bp.executed is False
    assert not bp.error
    # the line did not stop: a later step still ran and the manifest completed.
    assert "deploy_sre_agent" in results
    assert manifest.executed is True


def test_apply_execute_branch_protection_other_403_still_fails(tmp_path, monkeypatch):
    """A non-plan 403 (e.g. permission) is not swallowed -> the step fails."""
    monkeypatch.chdir(tmp_path)

    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        if len(cmd) > 2 and "rulesets?includes_parents" in str(cmd[2]):
            raise subprocess.CalledProcessError(
                returncode=1, cmd=cmd, stderr="gh: Must have admin rights (HTTP 403)"
            )
        hit = _az_deploy(cmd, _AZURE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0)

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    bp = next(s for s in manifest.plan.steps if s.name == "branch_protection")
    assert bp.result == "failed"
    assert bp.error


def test_seed_repo_puts_baseline_ci_workflow_when_absent():
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if "--jq" in cmd and cmd[-1] == ".sha":
            # The workflow file does not exist yet -> gh api exits non-zero.
            raise subprocess.CalledProcessError(1, cmd, stderr="Not Found")
        return _completed(stdout="")

    prov = InstanceProvisioner(
        InstanceSpec(product="demo", owner="acme", creation_maturity="low"),
        run=fake_run,
    )
    prov._seed_repo()

    puts = [c for c in calls if "--method" in c and "PUT" in c]
    assert len(puts) == 1
    put = puts[0]
    assert put[:5] == [
        "gh", "api", "--method", "PUT",
        "/repos/acme/demo/contents/.github/workflows/ci.yml",
    ]
    assert "-f" in put and "branch=main" in put
    content = next(a for a in put if a.startswith("content="))[len("content="):]
    import base64

    assert base64.b64decode(content).decode("utf-8").startswith("name: ci")


def test_seed_repo_is_idempotent_when_workflow_present():
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if "--jq" in cmd and cmd[-1] == ".sha":
            return _completed(stdout="abc123\n")  # already seeded
        return _completed(stdout="")

    prov = InstanceProvisioner(
        InstanceSpec(product="demo", owner="acme", creation_maturity="low"),
        run=fake_run,
    )
    prov._seed_repo()

    assert not [c for c in calls if "--method" in c and "PUT" in c]


# ---------------------------------------------------------------------------
# Human owner/governance principal grants (adminPrincipalId + ownerPrincipalId)
# ---------------------------------------------------------------------------

_SIGNED_IN_USER_CMD = ["az", "ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"]
_OWNER_OID = "11111111-2222-3333-4444-555555555555"

_SRE_OUTPUTS_JSON = (
    "{" + _APPCONFIG_OUTPUT + ","
    ' "appInsightsId": {"type": "String", "value": "/sub/ai"},'
    ' "logAnalyticsId": {"type": "String", "value": "/sub/law"},'
    ' "keyVaultName": {"type": "String", "value": "kv1"}}'
)


def test_plan_includes_admin_principal_id_only_when_overridden():
    # Default plan() stays pure: no az lookup, so no adminPrincipalId is injected.
    plain = next(
        s for s in InstanceProvisioner(_spec()).plan().steps if s.name == "provision_azure"
    )
    assert not any(c.startswith("adminPrincipalId=") for c in plain.command)

    # An explicit override is threaded purely (no az call needed for plan()).
    overridden = next(
        s
        for s in InstanceProvisioner(_spec(), admin_principal_id=_OWNER_OID).plan().steps
        if s.name == "provision_azure"
    )
    assert f"adminPrincipalId={_OWNER_OID}" in overridden.command


def test_execute_threads_admin_and_owner_principal_ids(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        if cmd == _SIGNED_IN_USER_CMD:
            return _completed(stdout=f"{_OWNER_OID}\n")
        hit = _az_deploy(cmd, _SRE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    prov.apply(execute=True)

    group_create = next(c for c in calls if c[:4] == ["az", "deployment", "group", "create"])
    assert f"adminPrincipalId={_OWNER_OID}" in group_create

    sub_create = next(c for c in calls if c[:4] == ["az", "deployment", "sub", "create"])
    assert f"ownerPrincipalId={_OWNER_OID}" in sub_create


def test_admin_principal_id_override_beats_signed_in_user(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        if cmd == _SIGNED_IN_USER_CMD:
            return _completed(stdout="should-not-be-used\n")
        hit = _az_deploy(cmd, _SRE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(
        _spec(), run=fake_run, repo_root=tmp_path, admin_principal_id=_OWNER_OID
    )
    prov.apply(execute=True)

    # The override wins and short-circuits the signed-in-user lookup entirely.
    assert _SIGNED_IN_USER_CMD not in calls
    group_create = next(c for c in calls if c[:4] == ["az", "deployment", "group", "create"])
    assert f"adminPrincipalId={_OWNER_OID}" in group_create
    sub_create = next(c for c in calls if c[:4] == ["az", "deployment", "sub", "create"])
    assert f"ownerPrincipalId={_OWNER_OID}" in sub_create


def test_no_human_grant_when_signed_in_user_unavailable(tmp_path):
    # Service-principal / CI: `az ad signed-in-user show` fails -> both grants no-op so
    # provisioning still succeeds without any human principal.
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["gh", "repo", "view"]:
            return MagicMock(returncode=1)
        if cmd == _SIGNED_IN_USER_CMD:
            return _completed(returncode=1)
        hit = _az_deploy(cmd, _SRE_OUTPUTS_JSON)
        if hit is not None:
            return hit
        return MagicMock(returncode=0, stdout="")

    prov = InstanceProvisioner(_spec(), run=fake_run, repo_root=tmp_path)
    manifest = prov.apply(execute=True)

    group_create = next(c for c in calls if c[:4] == ["az", "deployment", "group", "create"])
    assert not any(c.startswith("adminPrincipalId=") for c in group_create)
    sub_create = next(c for c in calls if c[:4] == ["az", "deployment", "sub", "create"])
    assert not any(c.startswith("ownerPrincipalId=") for c in sub_create)
    assert manifest.executed is True


# ---------------------------------------------------------------------------
# infra/main.bicep role-assignment collision guard
#
# `dsf new` resolves adminPrincipalId from `az ad signed-in-user show`, which is the
# SAME principal as bicep's `deployer()` whenever a human runs provisioning locally.
# The admin and deployer grants target the same scope + role, so their deterministic
# `guid()` names collide ("resource defined multiple times") unless the deployer grant
# is skipped when it would duplicate the admin grant. These tests model that world so a
# regression is caught offline (CI has no Azure CLI to validate the template).
# ---------------------------------------------------------------------------

_ROLE_ASSIGNMENT_RE = re.compile(
    r"resource\s+(?P<sym>\w+)\s+'Microsoft\.Authorization/roleAssignments[^']*'\s*=\s*"
    r"(?:if\s*\((?P<cond>.*?)\)\s*)?\{(?P<body>.*?)\n\}",
    re.DOTALL,
)


def _role_assignments() -> list[dict[str, str | None]]:
    bicep = (_default_repo_root() / "infra" / "main.bicep").read_text()
    out: list[dict[str, str | None]] = []
    for m in _ROLE_ASSIGNMENT_RE.finditer(bicep):
        guid_args = ""
        for line in m.group("body").splitlines():
            if "name:" in line and "guid(" in line:
                guid_args = line.split("guid(", 1)[1].rsplit(")", 1)[0]
                break
        out.append({"sym": m.group("sym"), "cond": m.group("cond"), "guid_args": guid_args})
    return out


def _enabled_when_admin_is_deployer(cond: str | None) -> bool:
    """Evaluate a resource condition in the world where the deployer IS the admin.

    Substitutes ``adminPrincipalId`` and ``deployer().objectId`` with one non-empty,
    equal sentinel, then evaluates the (tiny) boolean vocabulary the template uses.
    Unknown gates (e.g. ``enableBingGrounding``) are treated as enabled — the
    conservative choice for a uniqueness assertion.
    """
    if cond is None:
        return True
    expr = cond.replace("deployer().objectId", "'P'").replace("adminPrincipalId", "'P'")
    expr = re.sub(r"toLower\(([^)]*)\)", r"(\1).lower()", expr)
    expr = re.sub(r"empty\(([^)]*)\)", r"(len(\1)==0)", expr)
    expr = expr.replace("||", " or ").replace("&&", " and ")
    expr = re.sub(r"!(?!=)", " not ", expr)
    try:
        return bool(eval(expr, {"__builtins__": {}}, {"len": len}))
    except NameError:
        return True


def _collision_key(guid_args: str) -> str:
    seed = guid_args.replace("deployer().objectId", "PRINCIPAL").replace(
        "adminPrincipalId", "PRINCIPAL"
    )
    return re.sub(r"\s+", "", seed)


def test_main_bicep_role_assignment_names_unique_when_admin_is_deployer():
    seen: dict[str, str] = {}
    collisions: list[tuple[str, str]] = []
    for ra in _role_assignments():
        if not _enabled_when_admin_is_deployer(ra["cond"]):
            continue
        key = _collision_key(ra["guid_args"] or "")
        if key in seen:
            collisions.append((seen[key], ra["sym"]))
        seen[key] = ra["sym"]
    assert not collisions, (
        "infra/main.bicep role assignments collide on the same guid() name when the "
        "deployer is the admin (adminPrincipalId == deployer().objectId), which ARM "
        f"rejects as a duplicate resource: {collisions}"
    )


def test_main_bicep_deployer_grants_guard_against_admin_collision():
    deployer_grants = [
        ra for ra in _role_assignments() if "deployer().objectId" in (ra["guid_args"] or "")
    ]
    assert deployer_grants, "expected at least one deployer-keyed role assignment in main.bicep"
    for ra in deployer_grants:
        cond = ra["cond"]
        assert cond and "adminPrincipalId" in cond, (
            f"{ra['sym']} keys its guid() name on deployer().objectId but is not guarded "
            "against the admin==deployer collision (its condition must reference "
            "adminPrincipalId so the redundant grant is skipped)"
        )
