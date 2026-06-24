"""Tests for instance spec models and defaults."""

from __future__ import annotations

import pytest
from pydantic import ValidationError

from dsf.instance.spec import (
    AzureProvisionResult,
    GitHubAppBinding,
    InstanceManifest,
    InstancePlan,
    InstanceSpec,
    ProvisionStep,
    default_label_taxonomy,
    instances_dir,
    manifest_path,
    read_manifest,
    write_manifest,
)


def test_default_label_taxonomy_shape():
    tax = default_label_taxonomy()
    assert set(tax) == {"type", "area", "severity"}
    assert "feature" in tax["type"]
    assert "sev-critical" in tax["severity"]


def test_instance_spec_defaults():
    spec = InstanceSpec(product="demo", owner="acme")
    assert spec.visibility == "private"
    assert spec.runtime_target == "aca"
    assert spec.confidence_threshold == 0.6
    assert spec.label_taxonomy == default_label_taxonomy()


def test_instance_spec_derivations():
    spec = InstanceSpec(product="demo", owner="acme")
    assert spec.resolved_repo() == "demo"
    assert spec.github_repo() == "acme/demo"
    assert spec.resource_group() == "rg-dsf-demo"


def test_instance_spec_explicit_repo_override():
    spec = InstanceSpec(product="demo", owner="acme", repo="demo-app")
    assert spec.resolved_repo() == "demo-app"
    assert spec.github_repo() == "acme/demo-app"


def test_provision_step_defaults():
    step = ProvisionStep(name="x", description="does x")
    assert step.command == []
    assert step.cwd == ""
    assert step.deferred is False
    assert step.executed is False
    assert step.result == ""


def test_manifest_round_trip(tmp_path):
    spec = InstanceSpec(product="demo", owner="acme")
    plan = InstancePlan(
        product="demo",
        steps=[ProvisionStep(name="write_config", description="write manifest")],
    )
    manifest = InstanceManifest(spec=spec, plan=plan, executed=False)

    path = write_manifest(manifest, repo_root=tmp_path)

    assert path == manifest_path("demo", repo_root=tmp_path)
    assert path == instances_dir(tmp_path) / "demo.json"
    assert path.exists()

    loaded = read_manifest("demo", repo_root=tmp_path)
    assert loaded.spec.product == "demo"
    assert loaded.spec.github_repo() == "acme/demo"
    assert loaded.plan.steps[0].name == "write_config"


def test_instance_spec_azure_defaults():
    spec = InstanceSpec(product="demo", owner="acme")
    assert spec.name_prefix == "dsf"
    assert spec.environment == "dev"
    assert spec.location == "swedencentral"
    assert spec.runtime_image == "ghcr.io/joranbergfeld/dsf-runtime:latest"
    assert spec.deployment_name() == "dsf-demo"


def test_instance_spec_rejects_bad_name_prefix():
    with pytest.raises(ValidationError):
        InstanceSpec(product="demo", owner="acme", name_prefix="1bad")


def test_azure_provision_result_round_trips_in_manifest(tmp_path):
    spec = InstanceSpec(product="demo", owner="acme")
    plan = InstancePlan(product="demo", steps=[])
    azure = AzureProvisionResult(
        resource_group="rg-dsf-demo",
        deployment_name="dsf-demo",
        location="swedencentral",
        outputs={"cosmosEndpoint": "https://x"},
    )
    manifest = InstanceManifest(spec=spec, plan=plan, executed=True, azure=azure)
    write_manifest(manifest, repo_root=tmp_path)

    loaded = read_manifest("demo", repo_root=tmp_path)
    assert loaded.azure is not None
    assert loaded.azure.outputs["cosmosEndpoint"] == "https://x"
    assert loaded.azure.deployment_name == "dsf-demo"


def test_creation_maturity_defaults_to_low():
    spec = InstanceSpec(product="demo", owner="acme")
    assert spec.creation_maturity == "low"


def test_creation_maturity_accepts_high():
    spec = InstanceSpec(product="demo", owner="acme", creation_maturity="high")
    assert spec.creation_maturity == "high"


def test_manifest_round_trips_github_app_binding():
    spec = InstanceSpec(product="demo", owner="acme")
    plan = InstancePlan(product="demo", steps=[])
    binding = GitHubAppBinding(app_id="123", installation_id="456", repository_id=789)
    manifest = InstanceManifest(spec=spec, plan=plan, github_app=binding)

    loaded = InstanceManifest.model_validate_json(manifest.model_dump_json())
    assert loaded.github_app is not None
    assert loaded.github_app.app_id == "123"
    assert loaded.github_app.installation_id == "456"
    assert loaded.github_app.repository_id == 789
    assert loaded.github_app.private_key_secret == "github-app-private-key"


def test_manifest_github_app_defaults_to_none():
    spec = InstanceSpec(product="demo", owner="acme")
    plan = InstancePlan(product="demo", steps=[])
    assert InstanceManifest(spec=spec, plan=plan).github_app is None


def test_creation_maturity_rejects_unknown_value():
    with pytest.raises(ValidationError):
        InstanceSpec(product="demo", owner="acme", creation_maturity="medium")


# --- SRE agent spec tests ---


def _spec(**kw):
    return InstanceSpec(product="microbi", owner="acme", **kw)


def test_sre_agent_location_defaults_to_sweden_central():
    assert _spec().sre_agent_location == "swedencentral"


def test_sre_agent_location_rejects_unsupported_region():
    with pytest.raises(ValueError, match="sre_agent_location"):
        _spec(sre_agent_location="westeurope")


def test_sre_agent_location_accepts_all_offered_regions():
    # The live Microsoft.App/agents provider offers 7 regions (issue #63).
    for region in (
        "swedencentral",
        "uksouth",
        "eastus2",
        "australiaeast",
        "francecentral",
        "canadacentral",
        "koreacentral",
    ):
        assert _spec(sre_agent_location=region).sre_agent_location == region


def test_sre_agent_name_and_rg():
    s = _spec()
    assert s.sre_agent_name() == "dsf-sre-microbi"
    assert s.sre_resource_group() == "rg-dsf-sre-microbi"


def test_monitored_rgs_defaults_to_factory_rg():
    assert _spec().monitored_rgs() == ["rg-dsf-microbi"]


def test_monitored_rgs_appends_and_dedupes():
    s = _spec(monitored_resource_groups=["rg-app", "rg-dsf-microbi", "rg-app"])
    assert s.monitored_rgs() == ["rg-dsf-microbi", "rg-app"]
