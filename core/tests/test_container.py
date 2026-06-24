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


def test_settings_parse_github_app_env():
    from dsf.container import AzureRuntimeSettings

    settings = AzureRuntimeSettings.from_env(
        {
            "DSF_PRODUCT": "demo",
            "GITHUB_APP_ID": "42",
            "GITHUB_INSTALLATION_ID": "9001",
            "GITHUB_APP_PRIVATE_KEY_SECRET": "github-app-private-key",
        }
    )
    assert settings.github_app_id == "42"
    assert settings.github_installation_id == "9001"
    assert settings.github_app_private_key_secret == "github-app-private-key"


def test_settings_github_app_fields_default_blank():
    from dsf.container import AzureRuntimeSettings

    settings = AzureRuntimeSettings.from_env({"DSF_PRODUCT": "demo"})
    assert settings.github_app_id == ""
    assert settings.github_installation_id == ""


def test_settings_parse_github_repository_env():
    from dsf.container import AzureRuntimeSettings

    settings = AzureRuntimeSettings.from_env(
        {"DSF_PRODUCT": "demo", "GITHUB_REPOSITORY": "acme/demo"}
    )
    assert settings.github_repository == "acme/demo"


def test_select_github_client_uses_app_when_configured():
    from dsf.container import AzureRuntimeSettings, _select_github_client
    from dsf.github_app_client import GitHubAppClient

    settings = AzureRuntimeSettings(
        product="demo",
        keyvault_uri="https://kv.example",
        github_app_id="42",
        github_installation_id="9001",
        github_app_private_key_secret="github-app-private-key",
        github_repository="acme/demo",
    )
    client = _select_github_client(settings, key_reader=lambda uri, name: "PEM")

    assert isinstance(client, GitHubAppClient)
    assert client.app_id == "42"
    assert client.installation_id == "9001"
    assert client.private_key_pem == "PEM"
    assert client.repositories == ["demo"]


def test_select_github_client_requires_repository_when_app_configured():
    from dsf.container import AzureRuntimeSettings, _select_github_client

    settings = AzureRuntimeSettings(
        product="demo",
        keyvault_uri="https://kv.example",
        github_app_id="42",
        github_installation_id="9001",
        github_app_private_key_secret="github-app-private-key",
        github_repository="",
    )
    with pytest.raises(ValueError, match="GITHUB_REPOSITORY"):
        _select_github_client(settings, key_reader=lambda uri, name: "PEM")


def test_select_github_client_falls_back_without_app():
    from dsf.container import AzureRuntimeSettings, _select_github_client
    from dsf.github_client import RealGitHubClient

    settings = AzureRuntimeSettings(product="demo")
    client = _select_github_client(settings, key_reader=lambda uri, name: "PEM")

    assert isinstance(client, RealGitHubClient)
