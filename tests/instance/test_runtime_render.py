"""Tests for render_runtime_bundle (per-product runtime scaffolding)."""

from __future__ import annotations

from dsf.instance.provisioner import InstanceProvisioner
from dsf.instance.runtime_render import render_runtime_bundle, runtime_dir
from dsf.instance.spec import AzureProvisionResult, InstanceManifest, InstanceSpec


def _manifest(tmp_path, *, with_azure: bool = True) -> InstanceManifest:
    spec = InstanceSpec(product="microbi", owner="acme", name_prefix="microbi")
    plan = InstanceProvisioner(spec, repo_root=tmp_path).plan()
    azure = (
        AzureProvisionResult(
            resource_group="rg-dsf-microbi",
            deployment_name="dsf-microbi",
            location="swedencentral",
            outputs={
                "appConfigEndpoint": "https://ac.example",
                "keyVaultUri": "https://kv.example",
                "appInsightsConnectionString": "InstrumentationKey=abc;IngestionEndpoint=https://i.example",
                "cosmosEndpoint": "https://cosmos.example",
            },
        )
        if with_azure
        else None
    )
    return InstanceManifest(spec=spec, plan=plan, executed=with_azure, azure=azure)


def test_render_writes_compose_and_env_under_runtime_dir(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    assert bundle.runtime_dir == runtime_dir("microbi", tmp_path)
    assert bundle.runtime_dir == tmp_path / "config" / "instances" / "microbi.runtime"
    assert bundle.compose_path.is_file()
    assert bundle.env_path.is_file()
    assert bundle.compose_path.name == "compose.orchestrator.yml"
    assert bundle.env_path.name == ".env.orchestrator"


def test_render_env_scopes_product_and_maps_endpoints(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    env = bundle.env_path.read_text(encoding="utf-8")
    assert "DSF_MODE=azure" in env
    assert "DSF_PRODUCT=microbi" in env
    assert "AZURE_APPCONFIG_ENDPOINT=https://ac.example" in env
    assert "AZURE_KEYVAULT_URI=https://kv.example" in env
    assert "AZURE_COSMOS_ENDPOINT=https://cosmos.example" in env
    assert "APPLICATIONINSIGHTS_CONNECTION_STRING=InstrumentationKey=abc" in env


def test_render_compose_scopes_container_and_references_env_file(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    compose = bundle.compose_path.read_text(encoding="utf-8")
    assert "dsf-orchestrator-microbi" in compose
    assert ".env.orchestrator" in compose
    assert "src/dsf/runtime/Dockerfile" in compose


def test_render_does_not_inline_secrets(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    env = bundle.env_path.read_text(encoding="utf-8")
    # bearer/GitHub tokens are runtime-injected (Key Vault / managed identity),
    # never rendered into the bundle:
    assert "A2A_BEARER_TOKEN" not in env
    assert "GITHUB_TOKEN" not in env
    assert "GH_TOKEN" not in env


def test_render_tolerates_missing_azure_outputs(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path, with_azure=False), repo_root=tmp_path)
    env = bundle.env_path.read_text(encoding="utf-8")
    assert "DSF_PRODUCT=microbi" in env
    # endpoints are blank (not yet provisioned) but the keys are present:
    assert "AZURE_APPCONFIG_ENDPOINT=" in env
    assert "AZURE_COSMOS_ENDPOINT=" in env
