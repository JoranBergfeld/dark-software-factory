from __future__ import annotations

import sys

import pytest

from dsf.container import (
    AzureRuntimeSettings,
    build_charter_store,
    build_config_store,
    build_model_client,
    build_repo_app_client,
)


def test_build_repo_app_client_scopes_token_to_repo():
    settings = AzureRuntimeSettings(
        product="alpha",
        github_app_id="1",
        github_installation_id="2",
        keyvault_uri="https://kv.vault.azure.net",
        github_app_private_key_secret="pem-secret",
        github_repository="org/alpha",
    )
    client = build_repo_app_client(settings, key_reader=lambda uri, name: "dummy-pem")
    assert client.repositories == ["alpha"]


def test_build_repo_app_client_raises_when_unconfigured():
    with pytest.raises(ValueError):
        build_repo_app_client(AzureRuntimeSettings(product="alpha"))


def test_build_charter_store_is_sdk_free():
    settings = AzureRuntimeSettings(product="alpha", cosmos_endpoint="https://c.documents.azure.com")
    store = build_charter_store(settings)
    assert store.__class__.__name__ == "CosmosCharterStore"
    assert "azure.cosmos" not in sys.modules


def test_build_charter_store_raises_without_cosmos_endpoint():
    with pytest.raises(ValueError):
        build_charter_store(AzureRuntimeSettings(product="alpha"))


def test_build_model_client_raises_when_unconfigured():
    with pytest.raises(ValueError):
        build_model_client(AzureRuntimeSettings(product="alpha"))


def test_build_config_store_raises_without_appconfig_endpoint():
    with pytest.raises(ValueError):
        build_config_store(AzureRuntimeSettings(product="alpha"))
