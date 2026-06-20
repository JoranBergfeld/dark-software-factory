"""Tests for the real-only service container fail-loud contract."""

from __future__ import annotations

import pytest

from dsf.container import AzureRuntimeSettings, build_services


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


def test_build_services_requires_product():
    with pytest.raises(ValueError):
        build_services(env={})


def test_build_services_missing_endpoints_names_every_missing_var():
    with pytest.raises(ValueError) as exc:
        build_services(env={"DSF_PRODUCT": "microbi"})

    message = str(exc.value)
    for var in (
        "AZURE_APPCONFIG_ENDPOINT",
        "AZURE_COSMOS_ENDPOINT",
        "AZURE_OPENAI_ENDPOINT",
        "AZURE_OPENAI_DEPLOYMENT",
        "AZURE_OPENAI_EMBEDDING_DEPLOYMENT",
    ):
        assert var in message


def test_build_services_partial_endpoints_names_only_the_missing():
    env = {
        "DSF_PRODUCT": "microbi",
        "AZURE_APPCONFIG_ENDPOINT": "https://ac.example",
        "AZURE_COSMOS_ENDPOINT": "https://cosmos.example",
        "AZURE_OPENAI_ENDPOINT": "https://x.openai.azure.com",
        # deployment + embedding deployment still missing
    }
    with pytest.raises(ValueError) as exc:
        build_services(env=env)

    message = str(exc.value)
    assert "AZURE_OPENAI_DEPLOYMENT" in message
    assert "AZURE_OPENAI_EMBEDDING_DEPLOYMENT" in message
    # The ones that are set must not be reported as missing.
    assert "AZURE_APPCONFIG_ENDPOINT" not in message
    assert "AZURE_COSMOS_ENDPOINT" not in message
