"""WebIQ-SDK web-search backend for :class:`WebIqMcpBackend`.

Builds an async ``search(query) -> list[dict]`` callable on top of the real
Microsoft **WebIQ** SDK (``pip install webiq``). The API key is resolved
app-side — ``WEBIQ_API_KEY`` env override, else read from the product Key Vault
(secret named by ``WEBIQ_API_KEY_SECRET``, default ``webiq-api-key``) at
``AZURE_KEYVAULT_URI`` — mirroring how the GitHub App private key is read. The
key is seeded into that vault at provision time (see
``InstanceProvisioner._seed_webiq_key``), so a deploy-time ACA secret reference
cannot work; the read happens here, at runtime.

Tests inject an ``httpx.AsyncClient`` (used by the SDK as-is) and a
``key_reader`` so the real SDK path runs with no network and no Azure.
"""

from __future__ import annotations

import os
from typing import TYPE_CHECKING

from dsf.agents.mode import env_required

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable

    import httpx

#: Per-result confidence. The WebIQ API exposes no per-result score, so we use
#: the same constant the prior web-research path used.
_DEFAULT_CONFIDENCE = 0.6
#: Default Key Vault secret name holding the WebIQ API key.
_DEFAULT_KEY_SECRET = "webiq-api-key"


def _read_kv_secret(vault_uri: str, secret_name: str) -> str:  # pragma: no cover - real Azure I/O
    """Read a secret value from Azure Key Vault via the ambient managed identity.

    Kept local (not imported from ``dsf.container``) so the agent stays
    self-contained and importing it triggers no heavy Azure imports; the Azure
    SDKs are imported lazily here and only on the real runtime path.
    """
    from azure.identity import DefaultAzureCredential
    from azure.keyvault.secrets import SecretClient

    client = SecretClient(vault_url=vault_uri, credential=DefaultAzureCredential())
    return client.get_secret(secret_name).value or ""


def _resolve_api_key(key_reader: Callable[[str, str], str] | None) -> str:
    """Resolve the WebIQ API key: env override, else Key Vault read."""
    direct = os.environ.get("WEBIQ_API_KEY")
    if direct:
        return direct
    vault_uri = env_required(
        "AZURE_KEYVAULT_URI", hint="product Key Vault URI holding the WebIQ API key"
    )
    secret_name = os.environ.get("WEBIQ_API_KEY_SECRET") or _DEFAULT_KEY_SECRET
    reader = key_reader or _read_kv_secret
    return reader(vault_uri, secret_name)


def _max_results() -> int:
    """Result cap from ``WEBIQ_MAX_RESULTS`` (default 5)."""
    try:
        return max(1, int(os.environ.get("WEBIQ_MAX_RESULTS") or "5"))
    except ValueError:
        return 5


def _build_webiq_search(
    *,
    client: httpx.AsyncClient | None = None,
    key_reader: Callable[[str, str], str] | None = None,
) -> Callable[[str], Awaitable[list[dict]]]:
    """Return an async ``search(query)`` backed by the WebIQ SDK.

    ``client`` (an ``httpx.AsyncClient``) is passed to the SDK as-is when given —
    tests inject a ``MockTransport``-backed client with the WebIQ ``base_url``.
    When ``None`` the SDK constructs its own client against the live endpoint.
    ``key_reader`` overrides the Key Vault read in tests.
    """
    from webiq import WebIQAsyncClient
    from webiq.types import ContentFormat

    api_key = _resolve_api_key(key_reader)
    sdk = WebIQAsyncClient(api_key=api_key, http_client=client)
    limit = _max_results()

    async def search(query: str) -> list[dict]:
        response = await sdk.web.search(
            query, max_results=limit, content_format=ContentFormat.text
        )
        results: list[dict] = []
        for r in response.webResults or []:
            finding = (r.content or r.title or "").strip()
            url = (r.url or "").strip()
            if not finding and not url:
                continue
            results.append(
                {"finding": finding, "url": url, "confidence": _DEFAULT_CONFIDENCE}
            )
        return results[:limit]

    return search


__all__ = ["_build_webiq_search"]
