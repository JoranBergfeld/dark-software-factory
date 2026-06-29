"""Tests for InstanceDeprovisioner.plan() and apply()."""

from __future__ import annotations

import json
import subprocess
from unittest.mock import MagicMock

import pytest

from dsf.instance.deprovisioner import InstanceDeprovisioner
from dsf.instance.provisioner import InstanceProvisioner
from dsf.instance.spec import (
    AzureProvisionResult,
    InstanceManifest,
    InstancePlan,
    InstanceSpec,
    TeardownPlan,
    manifest_path,
    write_manifest,
)


def _spec() -> InstanceSpec:
    return InstanceSpec(product="demo", owner="acme")


def _manifest(
    *,
    spec: InstanceSpec | None = None,
    azure: AzureProvisionResult | None = None,
) -> InstanceManifest:
    s = spec or _spec()
    return InstanceManifest(
        spec=s,
        plan=InstancePlan(product=s.product, steps=[]),
        executed=True,
        azure=azure,
    )


_DSF_GROUP_TAGS = json.dumps(
    {
        "project": "dark-software-factory",
        "managed-by": "dsf",
        "product": "demo",
        "component": "backing-services",
    }
)


def _group_show_tags(cmd, stdout=_DSF_GROUP_TAGS):
    """Answer the teardown guard's ``az group show --query tags`` probe.

    Returns a MagicMock carrying DSF tag JSON for a group-show command, else
    ``None`` so the caller can chain its own command handling.
    """
    if cmd[:3] == ["az", "group", "show"]:
        return MagicMock(returncode=0, stdout=stdout)
    return None


def _ok_run(cmd, **kwargs):
    """Default run mock: DSF-tagged group-show probe, success for everything else."""
    return _group_show_tags(cmd) or MagicMock(returncode=0)


# ---------------------------------------------------------------------------
# plan() — structure and content
# ---------------------------------------------------------------------------


def test_plan_step_order_and_names():
    plan = InstanceDeprovisioner(_manifest()).plan()
    assert isinstance(plan, TeardownPlan)
    assert plan.product == "demo"
    assert [s.name for s in plan.steps] == [
        "remove_sre_rbac",
        "delete_sre_agent",
        "delete_resource_group",
        "remove_runtime_index",
        "delete_config",
        "delete_repo",
    ]


def test_plan_step_order_delete_repo_is_last():
    """delete_repo MUST be the last step — Azure teardown before repo removal."""
    plan = InstanceDeprovisioner(_manifest()).plan()
    assert plan.steps[-1].name == "delete_repo"


def test_plan_delete_sre_agent_command():
    plan = InstanceDeprovisioner(_manifest()).plan()
    step = next(s for s in plan.steps if s.name == "delete_sre_agent")
    assert step.command == [
        "az", "group", "delete", "--name", "rg-dsf-sre-demo", "--yes",
    ]


def test_plan_delete_resource_group_command():
    plan = InstanceDeprovisioner(_manifest()).plan()
    step = next(s for s in plan.steps if s.name == "delete_resource_group")
    assert step.command == [
        "az", "group", "delete", "--name", "rg-dsf-demo", "--yes",
    ]


def test_plan_delete_repo_command():
    plan = InstanceDeprovisioner(_manifest()).plan()
    step = next(s for s in plan.steps if s.name == "delete_repo")
    assert step.command == ["gh", "repo", "delete", "acme/demo", "--yes"]


def test_plan_no_purge_step_by_default():
    plan = InstanceDeprovisioner(_manifest()).plan()
    assert not any(s.name == "purge_keyvault" for s in plan.steps)


def test_plan_purge_step_included_when_flag_set():
    azure = AzureProvisionResult(
        resource_group="rg-dsf-demo",
        deployment_name="dsf-demo",
        location="swedencentral",
        outputs={"keyVaultName": "kv-demoxyz"},
    )
    plan = InstanceDeprovisioner(_manifest(azure=azure), purge=True).plan()
    step = next((s for s in plan.steps if s.name == "purge_keyvault"), None)
    assert step is not None
    assert step.command == [
        "az", "keyvault", "purge", "--name", "kv-demoxyz", "--location", "swedencentral",
    ]


def test_plan_purge_step_skipped_when_no_azure_outputs():
    # No Azure result means we can't know the vault name — skip purge silently.
    plan = InstanceDeprovisioner(_manifest(azure=None), purge=True).plan()
    assert not any(s.name == "purge_keyvault" for s in plan.steps)


def test_plan_purge_step_falls_back_to_keyvaulturi():
    azure = AzureProvisionResult(
        resource_group="rg-dsf-demo",
        deployment_name="dsf-demo",
        location="swedencentral",
        outputs={"keyVaultUri": "https://kv-fallback.vault.azure.net/"},
    )
    plan = InstanceDeprovisioner(_manifest(azure=azure), purge=True).plan()
    step = next((s for s in plan.steps if s.name == "purge_keyvault"), None)
    assert step is not None
    assert "kv-fallback" in step.command


def test_plan_delete_repo_false_omits_step():
    plan = InstanceDeprovisioner(_manifest(), delete_repo=False).plan()
    assert not any(s.name == "delete_repo" for s in plan.steps)


# ---------------------------------------------------------------------------
# apply() — dry-run
# ---------------------------------------------------------------------------


def test_apply_dry_run_marks_all_steps_dry_run(tmp_path):
    m = _manifest()
    plan = InstanceDeprovisioner(m, repo_root=tmp_path).apply(execute=False)
    for step in plan.steps:
        assert step.result == "dry-run"


def test_apply_dry_run_makes_no_subprocess_calls(tmp_path):
    calls = []

    def fake_run(*a, **kw):
        calls.append(a)
        return MagicMock(returncode=0)

    m = _manifest()
    InstanceDeprovisioner(m, run=fake_run, repo_root=tmp_path).apply(execute=False)
    assert calls == []


def test_apply_dry_run_emits_start_and_done_per_step(tmp_path):
    events = []
    m = _manifest()
    InstanceDeprovisioner(m, repo_root=tmp_path).apply(
        execute=False,
        on_event=lambda *a: events.append(a),
    )
    phases = [(p, s.name) for p, _i, _t, s, _e in events]
    assert ("start", "delete_sre_agent") in phases
    assert ("done", "delete_sre_agent") in phases
    assert ("start", "delete_repo") in phases
    assert ("done", "delete_repo") in phases
    assert not any(p == "error" for p, *_ in events)


# ---------------------------------------------------------------------------
# apply() — execute
# ---------------------------------------------------------------------------


def test_apply_execute_runs_az_and_gh_commands(tmp_path):
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return _group_show_tags(cmd) or MagicMock(returncode=0)

    InstanceDeprovisioner(_manifest(), run=fake_run, repo_root=tmp_path).apply(execute=True)

    executed = calls
    assert ["az", "group", "delete", "--name", "rg-dsf-sre-demo", "--yes"] in executed
    assert ["az", "group", "delete", "--name", "rg-dsf-demo", "--yes"] in executed
    assert ["gh", "repo", "delete", "acme/demo", "--yes"] in executed


def test_apply_execute_order_azure_before_repo(tmp_path):
    """Azure teardown steps must precede GitHub repo deletion."""
    call_order = []

    def fake_run(cmd, **kwargs):
        if cmd[:2] == ["az", "group"]:
            call_order.append("azure")
        elif cmd[:3] == ["gh", "repo", "delete"]:
            call_order.append("repo")
        return _group_show_tags(cmd) or MagicMock(returncode=0)

    InstanceDeprovisioner(_manifest(), run=fake_run, repo_root=tmp_path).apply(execute=True)
    assert call_order.index("azure") < call_order.index("repo")

def test_deprovisioner_removes_runtime_index_entry():
    from dsf.config.owner_index import publish_runtime_config, read_runtime_config
    from dsf_testing.azure_doubles import InMemoryConfigGateway

    gateway = InMemoryConfigGateway()
    publish_runtime_config("https://o.azconfig.io", "demo", {"A": "1"}, gateway=gateway)

    deprv = InstanceDeprovisioner(
        _manifest(),
        owner_appconfig_endpoint="https://o.azconfig.io",
        appconfig_gateway=gateway,
    )
    step = next(s for s in deprv.plan().steps if s.name == "remove_runtime_index")
    deprv._execute_step(step)

    assert step.result == "removed"
    assert read_runtime_config("https://o.azconfig.io", "demo", gateway=gateway) == {}


def test_apply_execute_deletes_manifest_and_runtime(tmp_path):
    # Write a manifest and runtime bundle.
    m = _manifest()
    write_manifest(m, repo_root=tmp_path)
    runtime = tmp_path / "config" / "instances" / "demo.runtime"
    runtime.mkdir(parents=True)
    (runtime / "containerapp.yaml").write_text("# dummy", encoding="utf-8")

    InstanceDeprovisioner(
        m, run=_ok_run, repo_root=tmp_path
    ).apply(execute=True)

    assert not manifest_path("demo", tmp_path).exists()
    assert not runtime.exists()


def test_apply_execute_tolerates_already_absent_resource_group(tmp_path):
    """404-like errors from az group delete must not stop the teardown."""
    not_found_err = subprocess.CalledProcessError(
        1, ["az", "group", "delete"], stderr="ResourceGroupNotFound: rg-dsf-sre-demo"
    )

    def fake_run(cmd, **kwargs):
        hit = _group_show_tags(cmd)
        if hit is not None:
            return hit
        if cmd == ["az", "group", "delete", "--name", "rg-dsf-sre-demo", "--yes"]:
            raise not_found_err
        return MagicMock(returncode=0)

    plan = InstanceDeprovisioner(_manifest(), run=fake_run, repo_root=tmp_path).apply(execute=True)
    steps = {s.name: s for s in plan.steps}
    assert steps["delete_sre_agent"].result == "not-found (already absent)"
    # Teardown continues past the absent resource.
    assert steps["delete_resource_group"].result == "deleted"
    assert steps["delete_repo"].result == "executed"


def test_apply_execute_tolerates_already_deleted_repo(tmp_path):
    """A 404 from gh repo delete must not fail the teardown."""
    not_found_err = subprocess.CalledProcessError(
        1, ["gh", "repo", "delete"], stderr="Could not resolve to a repository"
    )

    def fake_run(cmd, **kwargs):
        hit = _group_show_tags(cmd)
        if hit is not None:
            return hit
        if cmd[:3] == ["gh", "repo", "delete"]:
            raise not_found_err
        return MagicMock(returncode=0)

    plan = InstanceDeprovisioner(_manifest(), run=fake_run, repo_root=tmp_path).apply(execute=True)
    step = next(s for s in plan.steps if s.name == "delete_repo")
    assert step.result == "not-found (already absent)"
    assert not any(s.result == "failed" for s in plan.steps)


def test_apply_execute_stops_on_real_error(tmp_path):
    real_error = subprocess.CalledProcessError(
        1, ["az", "group", "delete"], stderr="AuthorizationFailed: insufficient permissions"
    )

    def fake_run(cmd, **kwargs):
        hit = _group_show_tags(cmd)
        if hit is not None:
            return hit
        if cmd == ["az", "group", "delete", "--name", "rg-dsf-demo", "--yes"]:
            raise real_error
        return MagicMock(returncode=0)

    plan = InstanceDeprovisioner(_manifest(), run=fake_run, repo_root=tmp_path).apply(execute=True)
    steps = {s.name: s for s in plan.steps}
    assert steps["delete_resource_group"].result == "failed"
    assert "AuthorizationFailed" in steps["delete_resource_group"].error
    # Steps after the failure are left unrun.
    assert steps["delete_repo"].result == ""


def test_apply_execute_emits_error_event_on_failure(tmp_path):
    real_error = subprocess.CalledProcessError(
        1, ["az", "group", "delete"], stderr="AuthorizationFailed"
    )

    def fake_run(cmd, **kwargs):
        hit = _group_show_tags(cmd)
        if hit is not None:
            return hit
        if cmd == ["az", "group", "delete", "--name", "rg-dsf-demo", "--yes"]:
            raise real_error
        return MagicMock(returncode=0)

    events = []
    InstanceDeprovisioner(_manifest(), run=fake_run, repo_root=tmp_path).apply(
        execute=True, on_event=lambda *a: events.append(a)
    )

    errors = [e for e in events if e[0] == "error"]
    assert len(errors) == 1
    phase, _i, _t, step, exc = errors[0]
    assert step.name == "delete_resource_group"
    assert isinstance(exc, subprocess.CalledProcessError)


def test_apply_execute_purges_keyvault_when_flag_set(tmp_path):
    azure = AzureProvisionResult(
        resource_group="rg-dsf-demo",
        deployment_name="dsf-demo",
        location="swedencentral",
        outputs={"keyVaultName": "kv-demoxyz"},
    )
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return _group_show_tags(cmd) or MagicMock(returncode=0)

    InstanceDeprovisioner(
        _manifest(azure=azure), run=fake_run, repo_root=tmp_path, purge=True
    ).apply(execute=True)

    purge_calls = [c for c in calls if c[:3] == ["az", "keyvault", "purge"]]
    assert len(purge_calls) == 1
    assert "--name" in purge_calls[0]
    assert "kv-demoxyz" in purge_calls[0]



def test_apply_execute_refuses_untagged_resource_group(tmp_path):
    """A resource group not tagged managed-by=dsf must not be deleted."""
    foreign_tags = json.dumps({"project": "someone-else"})

    def fake_run(cmd, **kwargs):
        if cmd[:3] == ["az", "group", "show"]:
            return MagicMock(returncode=0, stdout=foreign_tags)
        if cmd[:3] == ["az", "group", "delete"]:
            raise AssertionError("must not delete a foreign resource group")
        return MagicMock(returncode=0)

    plan = InstanceDeprovisioner(_manifest(), run=fake_run, repo_root=tmp_path).apply(
        execute=True
    )
    steps = {s.name: s for s in plan.steps}
    assert steps["delete_sre_agent"].result == "failed"
    assert "managed-by=dsf" in steps["delete_sre_agent"].error
    # The line stops; later steps (incl. the product RG + repo) are left unrun.
    assert steps["delete_resource_group"].result == ""
    assert steps["delete_repo"].result == ""

# ---------------------------------------------------------------------------
# apply() — capture_output idempotency + SRE RBAC removal
# ---------------------------------------------------------------------------


def test_apply_execute_runs_delete_commands_with_captured_output(tmp_path):
    """Command steps must run with capture_output so is_not_found can read stderr.

    Mirrors real ``subprocess.run`` semantics: the CalledProcessError only carries
    ``stderr`` when ``capture_output=True`` was passed. Without the capture the
    already-absent resource would (incorrectly) fail the run. The ``delete_repo``
    command step is the canary here; resource-group deletes go through the
    tag-guarded path.
    """

    def fake_run(cmd, **kwargs):
        hit = _group_show_tags(cmd)
        if hit is not None:
            return hit
        if cmd[:3] == ["gh", "repo", "delete"]:
            stderr = "Could not resolve to a repository" if kwargs.get("capture_output") else None
            raise subprocess.CalledProcessError(1, cmd, stderr=stderr)
        return MagicMock(returncode=0, stdout="principal\n")

    plan = InstanceDeprovisioner(_manifest(), run=fake_run, repo_root=tmp_path).apply(execute=True)
    steps = {s.name: s for s in plan.steps}
    assert steps["delete_repo"].result == "not-found (already absent)"
    assert not any(s.result == "failed" for s in plan.steps)


def test_apply_execute_removes_sre_rbac_scopes(tmp_path):
    """delete must remove cross-RG + subscription SRE role assignments."""
    spec = InstanceSpec(product="demo", owner="acme", monitored_resource_groups=["rg-shared"])
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:4] == ["az", "identity", "show", "--resource-group"]:
            return MagicMock(returncode=0, stdout="principal-1\n")
        if cmd[:3] == ["az", "account", "show"]:
            return MagicMock(returncode=0, stdout="sub-xyz\n")
        return MagicMock(returncode=0, stdout="")

    plan = InstanceDeprovisioner(_manifest(spec=spec), run=fake_run, repo_root=tmp_path).apply(
        execute=True
    )
    steps = {s.name: s for s in plan.steps}
    assert steps["remove_sre_rbac"].result == "removed"

    role_deletes = [c for c in calls if c[:4] == ["az", "role", "assignment", "delete"]]
    scopes = {cmd[cmd.index("--scope") + 1] for cmd in role_deletes}
    assert "/subscriptions/sub-xyz" in scopes
    assert "/subscriptions/sub-xyz/resourceGroups/rg-dsf-demo" in scopes
    assert "/subscriptions/sub-xyz/resourceGroups/rg-shared" in scopes


def test_apply_execute_sre_rbac_tolerates_absent_identity(tmp_path):
    """An already-gone SRE identity records absent and does not stop the run."""

    def fake_run(cmd, **kwargs):
        hit = _group_show_tags(cmd)
        if hit is not None:
            return hit
        if cmd[:4] == ["az", "identity", "show", "--resource-group"]:
            return MagicMock(returncode=3, stdout="", stderr="ResourceNotFound")
        return MagicMock(returncode=0, stdout="")

    plan = InstanceDeprovisioner(_manifest(), run=fake_run, repo_root=tmp_path).apply(execute=True)
    steps = {s.name: s for s in plan.steps}
    assert steps["remove_sre_rbac"].result == "not-found (already absent)"
    assert not any(s.result == "failed" for s in plan.steps)


# ---------------------------------------------------------------------------
# from_product() factory
# ---------------------------------------------------------------------------

def test_from_product_loads_manifest(tmp_path):
    m = _manifest()
    write_manifest(m, repo_root=tmp_path)

    deprv = InstanceDeprovisioner.from_product("demo", repo_root=tmp_path)
    assert deprv.spec.product == "demo"
    assert deprv.spec.owner == "acme"


def test_from_product_raises_when_manifest_missing(tmp_path):
    with pytest.raises(FileNotFoundError):
        InstanceDeprovisioner.from_product("nonexistent", repo_root=tmp_path)


# ---------------------------------------------------------------------------
# Integration: round-trip new → delete
# ---------------------------------------------------------------------------


def test_roundtrip_new_then_delete_leaves_no_config_files(tmp_path):
    """Full provisioning dry-run followed by delete dry-run leaves no artifacts."""
    spec = _spec()
    prov = InstanceProvisioner(spec, run=MagicMock(returncode=0), repo_root=tmp_path)
    prov.apply(execute=False)
    assert manifest_path("demo", tmp_path).exists()

    # Now delete (dry-run) — config files should NOT be removed in dry-run.
    deprv = InstanceDeprovisioner.from_product("demo", repo_root=tmp_path)
    deprv.apply(execute=False)
    assert manifest_path("demo", tmp_path).exists()  # dry-run: file survives


def test_roundtrip_new_then_delete_execute_cleans_up(tmp_path):
    """After execute delete, manifest + runtime are gone from disk."""
    spec = _spec()
    prov = InstanceProvisioner(spec, run=MagicMock(returncode=0), repo_root=tmp_path)
    prov.apply(execute=False)
    assert manifest_path("demo", tmp_path).exists()

    deprv = InstanceDeprovisioner.from_product(
        "demo",
        run=_ok_run,
        repo_root=tmp_path,
    )
    deprv.apply(execute=True)
    assert not manifest_path("demo", tmp_path).exists()
    runtime = tmp_path / "config" / "instances" / "demo.runtime"
    assert not runtime.exists()
