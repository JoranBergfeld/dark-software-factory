"""The index payload carries endpoints + non-secret pointers, never secrets."""

from __future__ import annotations

from dsf.instance.runtime_index import runtime_index_values
from dsf.instance.spec import (
    AzureProvisionResult,
    GitHubAppBinding,
    InstanceManifest,
    InstancePlan,
    InstanceSpec,
)


def _manifest() -> InstanceManifest:
    spec = InstanceSpec(product="pets", owner="acme", repo="pets")
    azure = AzureProvisionResult(
        resource_group="rg-dsf-pets",
        deployment_name="dsf-pets",
        location="swedencentral",
        outputs={
            "appConfigEndpoint": "https://pets.azconfig.io",
            "keyVaultUri": "https://kv-pets.vault.azure.net/",
            "appInsightsConnectionString": "InstrumentationKey=abc",
            "cosmosEndpoint": "https://pets.documents.azure.com:443/",
            "openaiEndpoint": "https://pets.openai.azure.com/",
            "openaiDeployment": "gpt-4o",
            "openaiEmbeddingDeployment": "text-embedding-3-large",
        }
    )
    app = GitHubAppBinding(
        app_id="123",
        installation_id="456",
        repository_id=789,
        private_key_secret="dsf-app-private-key",
    )
    return InstanceManifest(
        spec=spec,
        plan=InstancePlan(product="pets", steps=[]),
        azure=azure,
        github_app=app,
    )


def test_payload_carries_endpoints_pointers_and_product():
    values = runtime_index_values(_manifest())

    assert values["AZURE_APPCONFIG_ENDPOINT"] == "https://pets.azconfig.io"
    assert values["AZURE_OPENAI_DEPLOYMENT"] == "gpt-4o"
    assert values["DSF_PRODUCT"] == "pets"
    assert values["GITHUB_REPOSITORY"] == "acme/pets"
    assert values["GITHUB_APP_ID"] == "123"
    assert values["GITHUB_INSTALLATION_ID"] == "456"
    assert values["GITHUB_APP_PRIVATE_KEY_SECRET"] == "dsf-app-private-key"
    assert values["WEBIQ_PROVIDER"] == "webiq"
    assert values["WEBIQ_API_KEY_SECRET"] == "webiq-api-key"


def test_payload_never_contains_secret_values():
    values = runtime_index_values(_manifest())
    joined = "\n".join(f"{k}={v}" for k, v in values.items())
    # The private-key *secret name* is allowed; PEM material is not.
    assert "BEGIN" not in joined
    assert "PRIVATE KEY-----" not in joined


def test_payload_omits_app_keys_when_no_binding():
    spec = InstanceSpec(product="pets", owner="acme", repo="pets")
    values = runtime_index_values(
        InstanceManifest(spec=spec, plan=InstancePlan(product="pets", steps=[]))
    )
    assert "GITHUB_APP_ID" not in values
    assert values["GITHUB_REPOSITORY"] == "acme/pets"
