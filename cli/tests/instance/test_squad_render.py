"""Tests for render_squad_bundle (per-product Ralph + KEDA manifests)."""

from __future__ import annotations

from dsf.instance.provisioner import InstanceProvisioner
from dsf.instance.runtime_render import runtime_dir
from dsf.instance.spec import AzureProvisionResult, InstanceManifest, InstanceSpec
from dsf.instance.squad_render import render_squad_bundle


def _manifest(tmp_path, *, azure: AzureProvisionResult | None = None) -> InstanceManifest:
    spec = InstanceSpec(product="microbi", owner="acme", name_prefix="microbi")
    plan = InstanceProvisioner(spec, repo_root=tmp_path).plan()
    return InstanceManifest(spec=spec, plan=plan, executed=azure is not None, azure=azure)


def _azure() -> AzureProvisionResult:
    return AzureProvisionResult(
        resource_group="rg-dsf-microbi",
        deployment_name="dsf-microbi",
        location="swedencentral",
        outputs={
            "squadIdentityClientId": "client-abc-123",
            "keyVaultName": "microbikv0001",
            "tenantId": "tenant-def-456",
        },
    )


def test_render_writes_three_manifests_under_squad_dir(tmp_path):
    bundle = render_squad_bundle(_manifest(tmp_path), repo_root=tmp_path)
    assert bundle.squad_dir == runtime_dir("microbi", tmp_path) / "squad"
    assert bundle.deployment_path.name == "ralph-deployment.yaml"
    assert bundle.scaledobject_path.name == "ralph-scaledobject.yaml"
    assert bundle.exporter_path.name == "issue-exporter.yaml"
    assert bundle.deployment_path.is_file()
    assert bundle.scaledobject_path.is_file()
    assert bundle.exporter_path.is_file()


def test_deployment_runs_ralph_watch_with_git_notes_backend(tmp_path):
    bundle = render_squad_bundle(_manifest(tmp_path), repo_root=tmp_path)
    text = bundle.deployment_path.read_text(encoding="utf-8")
    assert "squad" in text and "watch" in text and "--execute" in text
    assert "--state-backend" in text and "git-notes" in text
    assert "kind: Deployment" in text


def test_scaledobject_scales_zero_to_one_on_issue_count(tmp_path):
    bundle = render_squad_bundle(_manifest(tmp_path), repo_root=tmp_path)
    text = bundle.scaledobject_path.read_text(encoding="utf-8")
    assert "kind: ScaledObject" in text
    assert "minReplicaCount: 0" in text
    assert "maxReplicaCount: 1" in text
    assert "metrics-api" in text


def test_manifests_are_namespaced_to_the_product(tmp_path):
    bundle = render_squad_bundle(_manifest(tmp_path), repo_root=tmp_path)
    for path in (bundle.deployment_path, bundle.scaledobject_path, bundle.exporter_path):
        assert "microbi" in path.read_text(encoding="utf-8")


def test_identity_manifest_creates_the_namespace(tmp_path):
    bundle = render_squad_bundle(_manifest(tmp_path), repo_root=tmp_path)
    text = bundle.identity_path.read_text(encoding="utf-8")
    assert "kind: Namespace" in text
    assert "name: squad-microbi" in text


def test_identity_manifest_has_service_account_and_secretproviderclass(tmp_path):
    bundle = render_squad_bundle(_manifest(tmp_path), repo_root=tmp_path)
    text = bundle.identity_path.read_text(encoding="utf-8")
    assert "kind: ServiceAccount" in text
    assert "name: squad-microbi" in text
    assert "kind: SecretProviderClass" in text
    assert "secrets-store.csi.x-k8s.io" in text
    assert "github-token" in text


def test_secretproviderclass_carries_keyvault_outputs(tmp_path):
    bundle = render_squad_bundle(_manifest(tmp_path, azure=_azure()), repo_root=tmp_path)
    text = bundle.identity_path.read_text(encoding="utf-8")
    assert "client-abc-123" in text  # workload identity client id (SA + SPC)
    assert "microbikv0001" in text  # keyvaultName
    assert "tenant-def-456" in text  # tenantId


def test_dry_run_renders_identity_structure_with_blank_values(tmp_path):
    # No Azure outputs yet: the manifest still renders (preview), values blank.
    bundle = render_squad_bundle(_manifest(tmp_path, azure=None), repo_root=tmp_path)
    text = bundle.identity_path.read_text(encoding="utf-8")
    assert "kind: SecretProviderClass" in text
    assert "keyvaultName" in text and "clientID" in text and "tenantId" in text
    assert "client-abc-123" not in text


def test_both_pods_use_service_account_and_project_github_token(tmp_path):
    bundle = render_squad_bundle(_manifest(tmp_path), repo_root=tmp_path)
    for path in (bundle.deployment_path, bundle.exporter_path):
        text = path.read_text(encoding="utf-8")
        assert "serviceAccountName: squad-microbi" in text
        assert "secrets-store.csi.k8s.io" in text  # CSI volume driver
        assert "GITHUB_TOKEN" in text
        assert "secretProviderClass: squad-kv-microbi" in text


def test_render_writes_identity_manifest(tmp_path):
    bundle = render_squad_bundle(_manifest(tmp_path), repo_root=tmp_path)
    assert bundle.identity_path.name == "squad-identity.yaml"
    assert bundle.identity_path.is_file()
