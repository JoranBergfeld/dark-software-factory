"""Service container — wires ports to their real Azure-backed implementations."""

from __future__ import annotations

import os
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from typing import TYPE_CHECKING

from pydantic import BaseModel

from dsf.ports import (
    CharterStore,
    ConfigStore,
    GitHubClient,
    MemoryStore,
    ModelClient,
    Tracer,
)

if TYPE_CHECKING:
    from dsf.github_app_client import GitHubAppClient


class AzureRuntimeSettings(BaseModel):
    """Per-product runtime configuration resolved from the environment.

    ``product`` (``DSF_PRODUCT``) scopes the factory to a single product and is
    required. The data-plane endpoints (App Configuration / Cosmos / Azure
    OpenAI) are required too — :func:`build_services` validates them before it
    wires any adapter. ``keyvault_uri`` and ``appinsights_connection_string``
    are carried for the adapters that use them but are not validated here.
    """

    product: str
    appconfig_endpoint: str = ""
    keyvault_uri: str = ""
    appinsights_connection_string: str = ""
    cosmos_endpoint: str = ""
    openai_endpoint: str = ""
    openai_deployment: str = ""
    openai_embedding_deployment: str = ""
    github_app_id: str = ""
    github_installation_id: str = ""
    github_app_private_key_secret: str = ""
    github_repository: str = ""

    @classmethod
    def from_env(cls, env: Mapping[str, str]) -> AzureRuntimeSettings:
        """Resolve settings from ``env``. Raises ``ValueError`` if ``DSF_PRODUCT``
        is missing or blank — the runtime is meaningless without a product scope."""
        product = (env.get("DSF_PRODUCT") or "").strip()
        if not product:
            raise ValueError(
                "DSF_PRODUCT is required to scope the factory runtime "
                "(set DSF_PRODUCT=<product>)."
            )
        return cls(
            product=product,
            appconfig_endpoint=(env.get("AZURE_APPCONFIG_ENDPOINT") or "").strip(),
            keyvault_uri=(env.get("AZURE_KEYVAULT_URI") or "").strip(),
            appinsights_connection_string=(
                env.get("APPLICATIONINSIGHTS_CONNECTION_STRING") or ""
            ).strip(),
            cosmos_endpoint=(env.get("AZURE_COSMOS_ENDPOINT") or "").strip(),
            openai_endpoint=(env.get("AZURE_OPENAI_ENDPOINT") or "").strip(),
            openai_deployment=(env.get("AZURE_OPENAI_DEPLOYMENT") or "").strip(),
            openai_embedding_deployment=(
                env.get("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") or ""
            ).strip(),
            github_app_id=(env.get("GITHUB_APP_ID") or "").strip(),
            github_installation_id=(env.get("GITHUB_INSTALLATION_ID") or "").strip(),
            github_app_private_key_secret=(
                env.get("GITHUB_APP_PRIVATE_KEY_SECRET") or ""
            ).strip(),
            github_repository=(env.get("GITHUB_REPOSITORY") or "").strip(),
        )


@dataclass
class Services:
    """Bundle of every port instance for a running product factory."""

    model: ModelClient
    memory: MemoryStore
    config: ConfigStore
    github: GitHubClient
    tracer: Tracer
    charter: CharterStore
    product: str | None = None
    azure: AzureRuntimeSettings | None = None
    repo: GitHubAppClient | None = None


#: Required endpoint settings paired with the env var that supplies each one.
_REQUIRED_ENDPOINTS: tuple[tuple[str, str], ...] = (
    ("appconfig_endpoint", "AZURE_APPCONFIG_ENDPOINT"),
    ("cosmos_endpoint", "AZURE_COSMOS_ENDPOINT"),
    ("openai_endpoint", "AZURE_OPENAI_ENDPOINT"),
    ("openai_deployment", "AZURE_OPENAI_DEPLOYMENT"),
    ("openai_embedding_deployment", "AZURE_OPENAI_EMBEDDING_DEPLOYMENT"),
)


def _read_kv_secret(keyvault_uri: str, secret_name: str) -> str:
    """Read a Key Vault secret's value (real adapter; deferred Azure import)."""
    from azure.identity import DefaultAzureCredential
    from azure.keyvault.secrets import SecretClient

    client = SecretClient(vault_url=keyvault_uri, credential=DefaultAzureCredential())
    value = client.get_secret(secret_name).value
    if not value:
        raise ValueError(f"Key Vault secret {secret_name!r} is empty or unset")
    return value


def _select_github_client(
    settings: AzureRuntimeSettings,
    *,
    key_reader: Callable[[str, str], str] = _read_kv_secret,
) -> GitHubClient:
    """Return the App-backed client when the App is fully configured, else gh fallback.

    App path (preferred): app id + installation id + Key Vault uri + secret name all
    set. The private key is read from Key Vault and minted tokens are scoped to the
    single product repo by name (``GITHUB_REPOSITORY`` -> repo name). Otherwise falls
    back to the gh-CLI ``RealGitHubClient`` (local/dev, no App).
    """
    app_configured = (
        settings.github_app_id
        and settings.github_installation_id
        and settings.keyvault_uri
        and settings.github_app_private_key_secret
    )
    if app_configured:
        from dsf.github_app_client import GitHubAppClient

        repo_name = settings.github_repository.split("/")[-1]
        if not repo_name:
            raise ValueError(
                "GITHUB_REPOSITORY is required when the GitHub App is configured, to "
                "scope installation tokens to the single product repo (refusing to mint "
                "a token scoped to all installation repositories)"
            )
        return GitHubAppClient(
            app_id=settings.github_app_id,
            installation_id=settings.github_installation_id,
            private_key_pem=key_reader(
                settings.keyvault_uri, settings.github_app_private_key_secret
            ),
            repositories=[repo_name],
        )

    from dsf.github_client import RealGitHubClient

    return RealGitHubClient()


def build_services(*, env: Mapping[str, str] | None = None) -> Services:
    """Build a wired :class:`Services` bundle backed by real Azure adapters.

    Resolves :class:`AzureRuntimeSettings` from ``env`` (defaults to
    ``os.environ``), then **requires** every data-plane endpoint to be set. A
    missing ``DSF_PRODUCT`` or any blank endpoint raises ``ValueError`` naming
    the missing env vars — there is no offline/in-memory fallback. The Azure SDK
    imports are deferred so this module imports cleanly without them; they are
    only needed when ``build_services`` actually runs.
    """
    settings = AzureRuntimeSettings.from_env(env if env is not None else os.environ)

    missing = [
        var for attr, var in _REQUIRED_ENDPOINTS if not getattr(settings, attr)
    ]
    if missing:
        raise ValueError(
            "missing required Azure runtime configuration: "
            + ", ".join(missing)
        )
    from dsf.config.azure_store import AppConfigStore
    from dsf.memory.azure_store import CosmosMemoryStore
    from dsf.model.azure_client import AzureOpenAIModelClient
    from dsf.model.azure_embeddings import AzureOpenAIEmbeddingClient
    from dsf.observability.tracing import build_tracer

    embedder = AzureOpenAIEmbeddingClient.from_endpoint(
        settings.openai_endpoint,
        deployment=settings.openai_embedding_deployment,
    )
    config = AppConfigStore.from_endpoint(settings.appconfig_endpoint)
    memory = CosmosMemoryStore.from_endpoint(
        settings.cosmos_endpoint,
        database=settings.product,
        embedder=embedder,
    )
    model = AzureOpenAIModelClient.from_endpoint(
        settings.openai_endpoint, deployment=settings.openai_deployment
    )

    return Services(
        model=model,
        memory=memory,
        config=config,
        github=_select_github_client(settings),
        tracer=build_tracer(),
        product=settings.product,
        azure=settings,
    )
