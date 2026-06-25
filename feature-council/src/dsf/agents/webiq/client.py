"""Live WebIQ web-search client for :class:`WebIqMcpBackend`.

Builds an async ``search(query) -> list[dict]`` callable backed by a real
web-research provider, selected with the ``WEBIQ_PROVIDER`` env var:

* ``foundry`` (default; aliases ``azure`` / ``bing``) — Azure AI Foundry's
  *Grounding with Bing Search* tool, so WebIQ researches through the product's own
  Azure AI Foundry resource. Implemented in :mod:`dsf.agents.webiq.foundry`.
* ``tavily`` — the third-party Tavily web-search API (opt-in). Built here via
  ``httpx`` and constructed from ``TAVILY_API_KEY``.

The Tavily path accepts an injected ``httpx.AsyncClient`` so tests can drive it
with ``httpx.MockTransport`` and never touch the network.
"""

from __future__ import annotations

import os
from typing import TYPE_CHECKING

import httpx

from dsf.agents.mode import env_required

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable

_TAVILY_URL = "https://api.tavily.com/search"

#: ``WEBIQ_PROVIDER`` values that select the Azure AI Foundry grounding backend.
_FOUNDRY_PROVIDERS = frozenset({"foundry", "azure", "bing"})


def build_webiq_client_from_env(
    client: httpx.AsyncClient | None = None,
) -> Callable[[str], Awaitable[list[dict]]]:
    """Return an async ``search(query)`` backed by the configured provider.

    ``WEBIQ_PROVIDER`` (default ``foundry``) selects the backend:

    * ``foundry`` / ``azure`` / ``bing`` — Azure AI Foundry Grounding with Bing
      Search (see :func:`dsf.agents.webiq.foundry.build_foundry_search_from_env`).
      The ``client`` argument is ignored for this provider.
    * ``tavily`` — the Tavily web-search API (requires ``TAVILY_API_KEY``).

    Any other value raises :class:`NotImplementedError`.
    """
    provider = (os.environ.get("WEBIQ_PROVIDER") or "foundry").strip().lower()
    if provider in _FOUNDRY_PROVIDERS:
        from dsf.agents.webiq.foundry import build_foundry_search_from_env

        return build_foundry_search_from_env()
    if provider != "tavily":
        raise NotImplementedError(
            f"WEBIQ_PROVIDER {provider!r} not supported "
            "(use 'foundry' (default) or 'tavily')"
        )
    return _build_tavily_search_from_env(client)


def _build_tavily_search_from_env(
    client: httpx.AsyncClient | None = None,
) -> Callable[[str], Awaitable[list[dict]]]:
    """Return an async ``search(query)`` backed by the Tavily web-search API.

    Requires ``TAVILY_API_KEY``. When ``client`` is ``None`` a real
    ``httpx.AsyncClient`` is constructed with a 20s timeout; when injected (tests
    pass a ``MockTransport``-backed client) it is used as-is.
    """
    api_key = env_required("TAVILY_API_KEY", hint="Tavily web-search API key")

    if client is None:
        client = httpx.AsyncClient(timeout=20.0)

    async def search(query: str) -> list[dict]:
        resp = await client.post(
            _TAVILY_URL,
            json={
                "api_key": api_key,
                "query": query,
                "max_results": 5,
                "search_depth": "basic",
            },
        )
        resp.raise_for_status()

        data = resp.json()
        results: list[dict] = []
        for r in data.get("results", []) or []:
            results.append(
                {
                    "finding": r.get("content") or r.get("title") or "",
                    "url": r.get("url", ""),
                    "confidence": float(r.get("score", 0.5)),
                }
            )
        return results

    return search


__all__ = ["build_webiq_client_from_env"]
