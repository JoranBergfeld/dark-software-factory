"""Tests for render_runtime_bundle (per-product runtime scaffolding)."""

from __future__ import annotations

from dsf.instance.provisioner import InstanceProvisioner
from dsf.instance.runtime_render import (
    render_runtime_bundle,
    render_sre_summary,
    runtime_dir,
)
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


def test_render_writes_app_config_and_env_under_runtime_dir(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    assert bundle.runtime_dir == runtime_dir("microbi", tmp_path)
    assert bundle.runtime_dir == tmp_path / "config" / "instances" / "microbi.runtime"
    assert bundle.app_config_path.is_file()
    assert bundle.env_path.is_file()
    assert bundle.app_config_path.name == "containerapp.yaml"
    assert bundle.env_path.name == ".env.orchestrator"


def test_render_env_scopes_product_and_maps_endpoints(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    env = bundle.env_path.read_text(encoding="utf-8")
    assert "DSF_MODE" not in env  # no mode concept anymore (ADR 0014)
    assert "DSF_PRODUCT=microbi" in env
    assert "AZURE_APPCONFIG_ENDPOINT=https://ac.example" in env
    assert "AZURE_KEYVAULT_URI=https://kv.example" in env
    assert "AZURE_COSMOS_ENDPOINT=https://cosmos.example" in env
    assert "APPLICATIONINSIGHTS_CONNECTION_STRING=InstrumentationKey=abc" in env


def test_render_app_config_scopes_product(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    app = bundle.app_config_path.read_text(encoding="utf-8")
    assert "dsf-orchestrator-microbi" in app
    assert "image:" in app
    assert "DSF_PRODUCT" in app
    assert "microbi" in app


def test_render_does_not_inline_secrets(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path), repo_root=tmp_path)
    env = bundle.env_path.read_text(encoding="utf-8")
    # bearer/GitHub tokens are read at runtime from Key Vault via the ACA managed
    # identity (ADR 0004), never rendered into the bundle:
    assert "A2A_BEARER_TOKEN" not in env
    assert "GITHUB_TOKEN" not in env
    assert "GH_TOKEN" not in env


def test_render_tolerates_missing_azure_outputs(tmp_path):
    bundle = render_runtime_bundle(_manifest(tmp_path, with_azure=False), repo_root=tmp_path)
    env = bundle.env_path.read_text(encoding="utf-8")
    assert "DSF_PRODUCT=microbi" in env
    assert "AZURE_APPCONFIG_ENDPOINT=" in env
    assert "AZURE_COSMOS_ENDPOINT=" in env

def test_render_sre_summary_writes_post_deploy_summary(tmp_path):
    summ = render_sre_summary(_manifest(tmp_path), repo_root=tmp_path)
    assert summ.runtime_dir == runtime_dir("microbi", tmp_path)
    assert summ.summary_path.name == "sre-agent.md"
    body = summ.summary_path.read_text(encoding="utf-8")
    assert "dsf-sre-microbi" in body     # agent name
    assert "rg-dsf-microbi" in body     # monitored RG
    assert "acme/microbi" in body       # product repo
    assert "squad:ready" in body        # handoff label preserved
    assert "containerapp" not in body   # no Container App deploy
    assert "wizard" not in body.lower()  # no interactive wizard framing
    assert "oauth" not in body.lower()   # no OAuth framing
    # Verify that {product} placeholder is interpolated in verify command
    assert "dsf-sre-microbi" in body
    assert "{product}" not in body


def test_sre_summary_instructs_incident_label(tmp_path):
    from dsf.contracts.handoff import INCIDENT_LABEL
    from dsf.instance.runtime_render import _render_sre_summary_md

    md = _render_sre_summary_md(
        product="microbi",
        agent_name="dsf-sre-microbi",
        monitored_rgs=["rg-dsf-microbi", "rg-app"],
        repo="example/microbi",
    )
    assert f"`{INCIDENT_LABEL}`" in md
    assert "council" in md.lower()


def test_product_from_spec_threads_azure_monitor_scope():
    from dsf.instance.runtime_render import _product_from_spec
    from dsf.instance.spec import InstanceSpec

    spec = InstanceSpec(product="microbi", owner="acme")
    product = _product_from_spec(spec)
    # Defaults to the product key so the telemetry source has a non-empty scope to
    # resolve in azure mode; the live Application Insights id is filled in during
    # observability onboarding.
    assert product.azure_monitor_scope == spec.product
