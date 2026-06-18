"""Live WebIQ web-search client (HTTP) for :class:`WebIqMcpBackend`.

Builds an async ``search(query)`` callable backed by a real web-search provider
via ``httpx``. Only Tavily is supported today; the provider is selected with the
``WEBIQ_PROVIDER`` env var (default ``tavily``).

The client is constructed from environment variables (see
``build_webiq_client_from_env``) but accepts an injected ``httpx.AsyncClient`` so
tests can drive it with ``httpx.MockTransport`` and never touch the network.
"""

from __future__ import annotations

import os
from typing import TYPE_CHECKING

import httpx

from dsf.agents.mode import env_required

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable

_TAVILY_URL = "https://api.tavily.com/search"


def build_webiq_client_from_env(
    client: httpx.AsyncClient | None = None,
) -> Callable[[str], Awaitable[list[dict]]]:
    """Return an async ``search(query)`` backed by a web-search provider.

    Env vars:

    * ``WEBIQ_PROVIDER`` (default ``tavily``) — which provider to use. Anything
      other than ``tavily`` raises :class:`NotImplementedError`.
    * ``TAVILY_API_KEY`` (required when provider is ``tavily``) — Tavily API key.

    When ``client`` is ``None`` a real ``httpx.AsyncClient`` is constructed with a
    20s timeout. When a client is injected (tests pass a ``MockTransport``-backed
    client) it is used as-is.
    """
    provider = (os.environ.get("WEBIQ_PROVIDER") or "tavily").strip().lower()
    if provider != "tavily":
        raise NotImplementedError(
            f"WEBIQ_PROVIDER {provider!r} not supported (only 'tavily')"
        )

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
