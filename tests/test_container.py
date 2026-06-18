"""Tests for the service container + CLI skeleton (plan Task 0.4)."""

from __future__ import annotations

import pytest

from dsf.config.store import InMemoryConfigStore
from dsf.container import AzureRuntimeSettings, Services, build_services
from dsf.fakes import (
    FakeMemoryStore,
    FakeModelClient,
)
from dsf.github_client import RealGitHubClient, RecordingGitHubClient
from dsf.observability.tracing import NoOpTracer
from dsf.ports import ConfigStore, GitHubClient, MemoryStore, ModelClient, Tracer


def test_build_services_local_wires_fakes():
    services = build_services("local")
    assert isinstance(services, Services)
    assert services.mode == "local"
    assert isinstance(services.model, FakeModelClient)
    assert isinstance(services.memory, FakeMemoryStore)
    assert isinstance(services.config, InMemoryConfigStore)
    assert isinstance(services.github, RecordingGitHubClient)
    assert isinstance(services.tracer, NoOpTracer)


def test_build_services_satisfy_protocols():
    services = build_services("local")
    assert isinstance(services.model, ModelClient)
    assert isinstance(services.memory, MemoryStore)
    assert isinstance(services.config, ConfigStore)
    assert isinstance(services.github, GitHubClient)
    assert isinstance(services.tracer, Tracer)


def test_azure_runtime_settings_from_env_requires_product():
    with pytest.raises(ValueError):
        AzureRuntimeSettings.from_env({})
    with pytest.raises(ValueError):
        AzureRuntimeSettings.from_env({"DSF_PRODUCT": "   "})


def test_azure_runtime_settings_from_env_reads_endpoints():
    settings = AzureRuntimeSettings.from_env(
        {
            "DSF_PRODUCT": "microbi",
            "AZURE_APPCONFIG_ENDPOINT": "https://ac.example",
            "AZURE_KEYVAULT_URI": "https://kv.example",
            "APPLICATIONINSIGHTS_CONNECTION_STRING": "InstrumentationKey=abc",
            "AZURE_COSMOS_ENDPOINT": "https://cosmos.example",
        }
    )
    assert settings.product == "microbi"
    assert settings.appconfig_endpoint == "https://ac.example"
    assert settings.keyvault_uri == "https://kv.example"
    assert settings.appinsights_connection_string == "InstrumentationKey=abc"
    assert settings.cosmos_endpoint == "https://cosmos.example"


def test_azure_runtime_settings_endpoints_optional():
    settings = AzureRuntimeSettings.from_env({"DSF_PRODUCT": "microbi"})
    assert settings.product == "microbi"
    assert settings.appconfig_endpoint == ""
    assert settings.cosmos_endpoint == ""


def test_services_has_product_and_azure_defaults_none():
    services = build_services("local")
    assert services.product is None
    assert services.azure is None


def test_build_services_gh_mode_uses_real_github_client():
    services = build_services("gh")
    assert isinstance(services, Services)
    assert services.mode == "gh"
    assert isinstance(services.github, RealGitHubClient)
    # Satisfies the port protocol.
    assert isinstance(services.github, GitHubClient)


def test_build_services_unknown_mode_raises():
    with pytest.raises(NotImplementedError):
        build_services("gcp")


def test_build_services_azure_wires_real_github_and_settings():
    from dsf.github_client import RealGitHubClient

    services = build_services("azure", env={"DSF_PRODUCT": "microbi"})
    assert services.mode == "azure"
    assert isinstance(services.github, RealGitHubClient)
    assert services.product == "microbi"
    assert services.azure is not None
    assert services.azure.product == "microbi"
    # model/memory/config remain fakes (the deferred-adapter seam):
    assert isinstance(services.model, FakeModelClient)
    assert isinstance(services.memory, FakeMemoryStore)
    assert isinstance(services.config, InMemoryConfigStore)
    # tracer comes from build_tracer("azure") and still satisfies the port:
    assert isinstance(services.tracer, Tracer)


def test_build_services_azure_missing_product_raises_value_error():
    with pytest.raises(ValueError):
        build_services("azure", env={})

