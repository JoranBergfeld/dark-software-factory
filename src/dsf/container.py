"""Service container — wires ports to implementations by mode."""

from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass

from pydantic import BaseModel

from dsf.config.store import InMemoryConfigStore
from dsf.fakes import (
    FakeModelClient,
)
from dsf.github_client import RecordingGitHubClient
from dsf.memory.store import InMemoryMemoryStore
from dsf.observability.tracing import NoOpTracer
from dsf.ports import (
    ConfigStore,
    GitHubClient,
    MemoryStore,
    ModelClient,
    Tracer,
)


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
    product: str | None = None
    azure: AzureRuntimeSettings | None = None


def build_services(
    mode: str = "local", *, env: Mapping[str, str] | None = None
) -> Services:
    """Build a wired :class:`Services` bundle.

    Supported modes
    ---------------
    ``local``
        Fully in-memory fakes — deterministic, no network calls, no credentials
        required.  All filing is dry-run unless explicitly overridden per-run.
    ``gh``
        Same fakes for model/memory/config/tracer, but with a real
        :class:`~dsf.github_client.RealGitHubClient` that calls the ``gh`` CLI.
        Requires ``gh`` to be authenticated in the environment.
    ``azure``
        Per-product runtime mode. Resolves :class:`AzureRuntimeSettings` from
        ``env`` (defaults to ``os.environ``; only ``DSF_PRODUCT`` is required),
        wires the real GitHub client and the OpenTelemetry tracer
        (:func:`dsf.observability.tracing.build_tracer`, which degrades to the
        fake tracer when OpenTelemetry is not installed), and keeps
        model/memory/config on fakes behind the deferred-adapter seam (SP3b).
        The resolved ``product``/``azure`` settings are carried on the bundle.
    """
    if mode == "local":
        return Services(
            mode=mode,
            model=FakeModelClient(),
            memory=InMemoryMemoryStore(),
            config=InMemoryConfigStore.from_defaults(),
            github=RecordingGitHubClient(),
            tracer=NoOpTracer(),
        )
    if mode == "gh":
        from dsf.github_client import RealGitHubClient

        return Services(
            mode=mode,
            model=FakeModelClient(),
            memory=InMemoryMemoryStore(),
            config=InMemoryConfigStore.from_defaults(),
            github=RealGitHubClient(),
            tracer=NoOpTracer(),
        )
    if mode == "azure":
        from dsf.github_client import RealGitHubClient
        from dsf.observability.tracing import build_tracer

        settings = AzureRuntimeSettings.from_env(env if env is not None else os.environ)
        return Services(
            mode=mode,
            model=FakeModelClient(),
            memory=InMemoryMemoryStore(),
            config=InMemoryConfigStore.from_defaults(),
            github=RealGitHubClient(),
            tracer=build_tracer("azure"),
            product=settings.product,
            azure=settings,
        )
    raise NotImplementedError(
        f"mode {mode!r} is not yet supported (available: 'local', 'gh', 'azure')."
    )
