"""Live FoundryIQ retrieve client (HTTP) for :class:`FoundryIqMcpBackend`.

Builds an async ``retrieve(query) -> list[dict]`` callable backed by an Azure AI
Search index (the knowledge index behind FoundryIQ) via ``httpx``. The backend
maps each returned chunk onto an :class:`~dsf.contracts.models.EvidenceItem`.

The client is constructed from environment variables (see
``build_foundryiq_client_from_env``) but accepts an injected
``httpx.AsyncClient`` so tests can drive it with ``httpx.MockTransport`` and
never touch the network.
"""

from __future__ import annotations

import os

import httpx

from dsf.agents.foundryiq.backend import RetrieveFn
from dsf.agents.mode import env_required

#: Azure AI Search REST API version pinned by the client.
_API_VERSION = "2024-07-01"


def _score_to_conf(s: object) -> float:
    """Map an Azure ``@search.score`` to a confidence in ``(0, 1)``."""
    score = float(s)  # type: ignore[arg-type]
    return min(1.0, score / (1.0 + score))


def build_foundryiq_client_from_env(
    client: httpx.AsyncClient | None = None,
) -> RetrieveFn:
    """Return an async ``retrieve(query)`` backed by an Azure AI Search index.

    Env vars:

    * ``AZURE_SEARCH_ENDPOINT`` (required) — e.g. ``https://<svc>.search.windows.net``.
    * ``AZURE_SEARCH_INDEX`` (required) — knowledge index name.
    * ``AZURE_SEARCH_KEY`` (required) — admin/query API key.
    * ``AZURE_SEARCH_CONTENT_FIELD`` (default ``content``) — chunk summary field.
    * ``AZURE_SEARCH_REF_FIELD`` (default ``url``) — chunk citation field.

    When ``client`` is ``None`` a real ``httpx.AsyncClient`` is constructed with
    the endpoint base URL and ``api-key`` header. When a client is injected
    (tests pass a ``MockTransport``-backed client) the ``api-key`` header is
    still applied to each request so the contract holds regardless of transport.
    """
    endpoint = env_required(
        "AZURE_SEARCH_ENDPOINT", hint="Azure AI Search service endpoint"
    )
    index = env_required("AZURE_SEARCH_INDEX", hint="Azure AI Search index name")
    key = env_required("AZURE_SEARCH_KEY", hint="Azure AI Search API key")
    content_field = os.environ.get("AZURE_SEARCH_CONTENT_FIELD") or "content"
    ref_field = os.environ.get("AZURE_SEARCH_REF_FIELD") or "url"

    if client is None:
        client = httpx.AsyncClient(
            base_url=endpoint,
            headers={"api-key": key, "Content-Type": "application/json"},
            timeout=20.0,
        )

    async def retrieve(query: str) -> list[dict]:
        url = f"/indexes/{index}/docs/search?api-version={_API_VERSION}"
        resp = await client.post(
            url,
            json={"search": query, "top": 5},
            headers={"api-key": key, "Content-Type": "application/json"},
        )
        resp.raise_for_status()

        body = resp.json()
        chunks: list[dict] = []
        for doc in body.get("value") or []:
            chunks.append(
                {
                    "summary": doc.get(content_field) or doc.get("content") or "",
                    "doc_ref": doc.get(ref_field) or doc.get("id") or "",
                    "confidence": _score_to_conf(doc.get("@search.score", 0.0)),
                }
            )
        return chunks

    return retrieve


__all__ = ["build_foundryiq_client_from_env"]
