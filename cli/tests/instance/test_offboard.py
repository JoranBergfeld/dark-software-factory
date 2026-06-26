"""Tests for InstanceOffboarder."""

from __future__ import annotations

import json
import subprocess
from unittest.mock import MagicMock

from dsf.config.registry import Product, load_registry, register_product
from dsf.instance.provisioner import InstanceOffboarder, InstanceProvisioner
from dsf.instance.spec import AzureProvisionResult, InstanceManifest, InstanceSpec, write_manifest

_DSF_GROUP_TAGS = json.dumps(
    {
        "project": "dark-software-factory",
        "managed-by": "dsf",
        "product": "demo",
        "component": "backing-services",
    }
)


def _seed_manifest(tmp_path, *, monitored_rgs: list[str] | None = None) -> InstanceManifest:
    spec = InstanceSpec(
        product="demo",
        owner="acme",
        name_prefix="demopfx12345",
        monitored_resource_groups=monitored_rgs or [],
    )
    plan = InstanceProvisioner(spec, repo_root=tmp_path).plan()
    manifest = InstanceManifest(
        spec=spec,
        plan=plan,
        executed=True,
        azure=AzureProvisionResult(
            resource_group=spec.resource_group(),
            deployment_name=spec.deployment_name(),
            location=spec.location,
            outputs={"keyVaultName": "demokv"},
        ),
    )
    write_manifest(manifest, repo_root=tmp_path)
    runtime = tmp_path / "config" / "instances" / "demo.runtime"
    runtime.mkdir(parents=True, exist_ok=True)
    (runtime / ".env.orchestrator").write_text("DSF_PRODUCT=demo\n", encoding="utf-8")
    register_product(
        Product(key="demo", github_repo="acme/demo"),
        path=tmp_path / "config" / "products.json",
    )
    return manifest


def test_offboard_plan_step_order_and_purge_default(tmp_path):
    _seed_manifest(tmp_path)
    plan = InstanceOffboarder("demo", repo_root=tmp_path).plan()
    assert [s.name for s in plan.steps] == [
        "remove_sre_rbac",
        "delete_sre_resource_group",
        "delete_product_resource_group",
        "purge_soft_deleted",
        "unregister_product",
        "remove_runtime_index",
        "remove_instance_artifacts",
    ]
    purge_step = next(s for s in plan.steps if s.name == "purge_soft_deleted")
    assert purge_step.deferred is True


def test_offboard_removes_runtime_index_entry(tmp_path):
    from dsf.config.owner_index import publish_runtime_config, read_runtime_config
    from dsf_testing.azure_doubles import InMemoryConfigGateway

    _seed_manifest(tmp_path)
    gateway = InMemoryConfigGateway()
    publish_runtime_config("https://o.azconfig.io", "demo", {"A": "1"}, gateway=gateway)

    off = InstanceOffboarder(
        "demo",
        repo_root=tmp_path,
        owner_appconfig_endpoint="https://o.azconfig.io",
        appconfig_gateway=gateway,
    )
    step = next(s for s in off.plan().steps if s.name == "remove_runtime_index")
    off._execute_step(step, execute=True)

    assert step.result == "removed"
    assert read_runtime_config("https://o.azconfig.io", "demo", gateway=gateway) == {}


def test_offboard_dry_run_runs_nothing(tmp_path):
    _seed_manifest(tmp_path)
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return MagicMock(returncode=0, stdout="")

    plan = InstanceOffboarder("demo", run=fake_run, repo_root=tmp_path).apply(execute=False)
    assert calls == []
    assert {s.name: s.result for s in plan.steps}["remove_sre_rbac"] == "dry-run"


def test_offboard_execute_removes_registry_and_artifacts(tmp_path):
    manifest = _seed_manifest(tmp_path, monitored_rgs=["rg-shared"])
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:4] == ["az", "identity", "show", "--resource-group"]:
            return MagicMock(returncode=0, stdout="1111-2222\n")
        if cmd[:4] == ["az", "account", "show", "--query"]:
            return MagicMock(returncode=0, stdout="sub123\n")
        if cmd[:3] == ["az", "group", "show"]:
            return MagicMock(returncode=0, stdout=_DSF_GROUP_TAGS)
        return MagicMock(returncode=0, stdout="")

    plan = InstanceOffboarder("demo", run=fake_run, repo_root=tmp_path).apply(execute=True)
    results = {s.name: s.result for s in plan.steps}
    assert results["remove_sre_rbac"] == "removed"
    assert results["delete_sre_resource_group"] == "deleted"
    assert results["delete_product_resource_group"] == "deleted"
    assert results["unregister_product"] == "unregistered"
    assert results["remove_instance_artifacts"] == "removed"

    registry = load_registry(tmp_path / "config" / "products.json")
    assert "demo" not in registry
    assert not (tmp_path / "config" / "instances" / "demo.json").exists()
    assert not (tmp_path / "config" / "instances" / "demo.runtime").exists()

    role_deletes = [c for c in calls if c[:4] == ["az", "role", "assignment", "delete"]]
    scopes = {cmd[cmd.index("--scope") + 1] for cmd in role_deletes}
    assert f"/subscriptions/sub123/resourceGroups/{manifest.spec.resource_group()}" in scopes
    assert "/subscriptions/sub123/resourceGroups/rg-shared" in scopes
    assert "/subscriptions/sub123" in scopes


def test_offboard_execute_tolerates_absent_resources(tmp_path):
    _seed_manifest(tmp_path)

    def fake_run(cmd, **kwargs):
        if cmd[:4] == ["az", "identity", "show", "--resource-group"]:
            return MagicMock(returncode=3, stdout="", stderr="ResourceNotFound")
        if cmd[:3] == ["az", "group", "show"]:
            return MagicMock(returncode=3, stdout="", stderr="ResourceGroupNotFound")
        return MagicMock(returncode=0, stdout="")

    plan = InstanceOffboarder("demo", run=fake_run, repo_root=tmp_path).apply(execute=True)
    results = {s.name: s.result for s in plan.steps}
    assert results["remove_sre_rbac"] == "not-found (already absent)"
    assert results["delete_sre_resource_group"] == "not-found (already absent)"
    assert results["delete_product_resource_group"] == "not-found (already absent)"


def test_offboard_purge_purges_soft_deleted_resources(tmp_path):
    _seed_manifest(tmp_path)
    calls = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:4] == ["az", "identity", "show", "--resource-group"]:
            return MagicMock(returncode=3, stdout="", stderr="ResourceNotFound")
        if cmd[:3] == ["az", "group", "show"]:
            return MagicMock(returncode=3, stdout="", stderr="ResourceGroupNotFound")
        if cmd[:3] == ["az", "keyvault", "list-deleted"]:
            return MagicMock(returncode=0, stdout="demokv\n")
        if cmd[:4] == ["az", "cognitiveservices", "account", "list-deleted"]:
            return MagicMock(returncode=0, stdout="demopfx12345abc\n")
        return MagicMock(returncode=0, stdout="")

    plan = InstanceOffboarder(
        "demo", run=fake_run, repo_root=tmp_path, purge=True
    ).apply(execute=True)
    purge = next(s for s in plan.steps if s.name == "purge_soft_deleted")
    assert "keyvault=yes" in purge.result
    assert "foundry=1" in purge.result
    assert ["az", "keyvault", "purge", "--name", "demokv", "--location", "swedencentral"] in calls
    assert [
        "az",
        "cognitiveservices",
        "account",
        "purge",
        "--name",
        "demopfx12345abc",
        "--location",
        "swedencentral",
        "--resource-group",
        "rg-dsf-demo",
    ] in calls


def test_offboard_purge_tolerates_absent_on_purge_race(tmp_path):
    _seed_manifest(tmp_path)

    def fake_run(cmd, **kwargs):
        if cmd[:4] == ["az", "identity", "show", "--resource-group"]:
            return MagicMock(returncode=3, stdout="", stderr="ResourceNotFound")
        if cmd[:3] == ["az", "group", "show"]:
            return MagicMock(returncode=3, stdout="", stderr="ResourceGroupNotFound")
        if cmd[:3] == ["az", "keyvault", "list-deleted"]:
            return MagicMock(returncode=0, stdout="demokv\n")
        if cmd[:4] == ["az", "cognitiveservices", "account", "list-deleted"]:
            return MagicMock(returncode=0, stdout="")
        if cmd[:3] == ["az", "keyvault", "purge"]:
            raise subprocess.CalledProcessError(
                3, cmd, output="", stderr="Vault not found"
            )
        return MagicMock(returncode=0, stdout="")

    plan = InstanceOffboarder(
        "demo", run=fake_run, repo_root=tmp_path, purge=True
    ).apply(execute=True)
    purge = next(s for s in plan.steps if s.name == "purge_soft_deleted")
    assert purge.result == "purged (keyvault=no, foundry=0)"
    assert not purge.error


def test_offboard_purge_skips_purge_protected_keyvault(tmp_path):
    """A purge-protected vault is reported as protected, not an error."""
    _seed_manifest(tmp_path)

    def fake_run(cmd, **kwargs):
        if cmd[:4] == ["az", "identity", "show", "--resource-group"]:
            return MagicMock(returncode=3, stdout="", stderr="ResourceNotFound")
        if cmd[:3] == ["az", "group", "show"]:
            return MagicMock(returncode=3, stdout="", stderr="ResourceGroupNotFound")
        if cmd[:3] == ["az", "keyvault", "list-deleted"]:
            return MagicMock(returncode=0, stdout="demokv\n")
        if cmd[:4] == ["az", "cognitiveservices", "account", "list-deleted"]:
            return MagicMock(returncode=0, stdout="")
        if cmd[:3] == ["az", "keyvault", "purge"]:
            raise subprocess.CalledProcessError(
                1,
                cmd,
                output="",
                stderr="(MethodNotAllowed) Operation 'DeletedVaultPurge' is not allowed.",
            )
        return MagicMock(returncode=0, stdout="")

    plan = InstanceOffboarder("demo", run=fake_run, repo_root=tmp_path, purge=True).apply(
        execute=True
    )
    purge = next(s for s in plan.steps if s.name == "purge_soft_deleted")
    assert purge.result == "purged (keyvault=protected, foundry=0)"
    assert not purge.error


def test_offboard_execute_refuses_untagged_resource_group(tmp_path):
    """Offboard must refuse to delete a resource group it did not tag."""
    _seed_manifest(tmp_path)
    foreign_tags = json.dumps({"managed-by": "terraform"})

    def fake_run(cmd, **kwargs):
        if cmd[:4] == ["az", "identity", "show", "--resource-group"]:
            return MagicMock(returncode=3, stdout="", stderr="ResourceNotFound")
        if cmd[:3] == ["az", "group", "show"]:
            return MagicMock(returncode=0, stdout=foreign_tags)
        if cmd[:3] == ["az", "group", "delete"]:
            raise AssertionError("must not delete a foreign resource group")
        return MagicMock(returncode=0, stdout="")

    plan = InstanceOffboarder("demo", run=fake_run, repo_root=tmp_path).apply(execute=True)
    results = {s.name: s.result for s in plan.steps}
    assert results["delete_sre_resource_group"] == "failed"
    # The line stops, so the product RG and registry/artifacts are left untouched.
    assert results["delete_product_resource_group"] == ""
    assert results["unregister_product"] == ""
    assert (tmp_path / "config" / "instances" / "demo.json").exists()
