"""Service container — wires ports to implementations by mode."""

from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass

from pydantic import BaseModel

from dsf.config.store import InMemoryConfigStore
from dsf.github_client import RecordingGitHubClient
from dsf.memory.store import InMemoryMemoryStore
from dsf.model import DeterministicModelClient
from dsf.observability.tracing import NoOpTracer
from dsf.ports import (
    ConfigStore,
    EmbeddingClient,
    GitHubClient,
    MemoryStore,
    ModelClient,
    SignalBuffer,
    Tracer,
)
from dsf.signals import InMemorySignalBuffer


class AzureRuntimeSettings(BaseModel):
    """Runtime configuration for ``azure`` mode, resolved from the environment.

    Only ``product`` (``DSF_PRODUCT``) is required — it scopes the factory to a
    single product. The endpoints are optional: they are carried for the real
    service adapters (Cosmos/App Config/App Insights) that land in SP3b, and are
    rendered into the per-product runtime bundle today.
    """

    product: str
    appconfig_endpoint: str = ""
    keyvault_uri: str = ""
    appinsights_connection_string: str = ""
    cosmos_endpoint: str = ""
    openai_endpoint: str = ""
    openai_deployment: str = ""
    openai_embedding_deployment: str = ""

    @classmethod
    def from_env(cls, env: Mapping[str, str]) -> AzureRuntimeSettings:
        """Resolve settings from ``env``. Raises ``ValueError`` if ``DSF_PRODUCT``
        is missing or blank — azure mode is meaningless without a product scope."""
        product = (env.get("DSF_PRODUCT") or "").strip()
        if not product:
            raise ValueError(
                "azure mode requires DSF_PRODUCT to scope the factory runtime "
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
        )


@dataclass
class Services:
    """Bundle of every port instance, selected per mode."""

    mode: str
    model: ModelClient
    memory: MemoryStore
    config: ConfigStore
    github: GitHubClient
    tracer: Tracer
    signals: SignalBuffer
    product: str | None = None
    azure: AzureRuntimeSettings | None = None


def build_services(
    mode: str = "local", *, env: Mapping[str, str] | None = None
) -> Services:
    """Build a wired :class:`Services` bundle.

    Supported modes
    ---------------
    ``local``
        Fully in-memory local implementations — deterministic, no network calls, no credentials
        required.  All filing is dry-run unless explicitly overridden per-run.
    ``gh``
        Same in-memory implementations for model/memory/config/tracer, but with a real
        :class:`~dsf.github_client.RealGitHubClient` that calls the ``gh`` CLI.
        Requires ``gh`` to be authenticated in the environment.
    ``azure``
        Per-product runtime mode. Resolves :class:`AzureRuntimeSettings` from
        ``env`` (defaults to ``os.environ``; only ``DSF_PRODUCT`` is required),
        wires the real GitHub client and the OpenTelemetry tracer
        (:func:`dsf.observability.tracing.build_tracer`, which degrades to the
        NoOpTracer when OpenTelemetry is not installed), and wires the real
        Azure data adapters (App Configuration / Cosmos / Azure OpenAI) for each
        configured endpoint, falling back to the in-memory sibling when an
        endpoint is unset (SP3b).
        The resolved ``product``/``azure`` settings are carried on the bundle.
    """
    if mode == "local":
        return Services(
            mode=mode,
            model=DeterministicModelClient(),
            memory=InMemoryMemoryStore(),
            config=InMemoryConfigStore.from_defaults(),
            github=RecordingGitHubClient(),
            tracer=NoOpTracer(),
            signals=InMemorySignalBuffer(),
        )
    if mode == "gh":
        from dsf.github_client import RealGitHubClient

        return Services(
            mode=mode,
            model=DeterministicModelClient(),
            memory=InMemoryMemoryStore(),
            config=InMemoryConfigStore.from_defaults(),
            github=RealGitHubClient(),
            tracer=NoOpTracer(),
            signals=InMemorySignalBuffer(),
        )
    if mode == "azure":
        from dsf.github_client import RealGitHubClient
        from dsf.observability.tracing import build_tracer

        settings = AzureRuntimeSettings.from_env(env if env is not None else os.environ)

        config: ConfigStore
        if settings.appconfig_endpoint:
            from dsf.config.azure_store import AppConfigStore

            config = AppConfigStore.from_endpoint(settings.appconfig_endpoint)
        else:
            config = InMemoryConfigStore.from_defaults()

        embedder: EmbeddingClient | None = None
        if settings.openai_endpoint and settings.openai_embedding_deployment:
            from dsf.model.azure_embeddings import AzureOpenAIEmbeddingClient

            embedder = AzureOpenAIEmbeddingClient.from_endpoint(
                settings.openai_endpoint,
                deployment=settings.openai_embedding_deployment,
            )

        memory: MemoryStore
        if settings.cosmos_endpoint:
            from dsf.memory.azure_store import CosmosMemoryStore

            memory = CosmosMemoryStore.from_endpoint(
                settings.cosmos_endpoint,
                database=settings.product,
                embedder=embedder,
            )
        else:
            memory = InMemoryMemoryStore(embedder=embedder)

        model: ModelClient
        if settings.openai_endpoint and settings.openai_deployment:
            from dsf.model.azure_client import AzureOpenAIModelClient

            model = AzureOpenAIModelClient.from_endpoint(
                settings.openai_endpoint, deployment=settings.openai_deployment
            )
        else:
            model = DeterministicModelClient()

        return Services(
            mode=mode,
            model=model,
            memory=memory,
            config=config,
            github=RealGitHubClient(),
            tracer=build_tracer("azure"),
            signals=InMemorySignalBuffer(),
            product=settings.product,
            azure=settings,
        )
    raise NotImplementedError(
        f"mode {mode!r} is not yet supported (available: 'local', 'gh', 'azure')."
    )
