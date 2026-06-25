"""Live WebIQ web-search client for :class:`WebIqMcpBackend`.

Builds an async ``search(query) -> list[dict]`` callable backed by a real
web-research provider, selected with the ``WEBIQ_PROVIDER`` env var:

* ``webiq`` (default) — the Microsoft **WebIQ** SDK (API-key auth). Implemented
  in :mod:`dsf.agents.webiq.webiq_sdk`.
* ``tavily`` — the third-party Tavily web-search API (opt-in). Built here via
  ``httpx`` and constructed from ``TAVILY_API_KEY``.

Both paths accept an injected ``httpx.AsyncClient`` so tests can drive them with
``httpx.MockTransport`` and never touch the network. Any other provider value
raises :class:`NotImplementedError`.
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
    key_reader: Callable[[str, str], str] | None = None,
) -> Callable[[str], Awaitable[list[dict]]]:
    """Return an async ``search(query)`` backed by the configured provider.

    ``WEBIQ_PROVIDER`` (default ``webiq``) selects the backend:

    * ``webiq`` — the Microsoft WebIQ SDK (see
      :func:`dsf.agents.webiq.webiq_sdk._build_webiq_search`). ``key_reader``
      overrides the Key Vault read in tests.
    * ``tavily`` — the Tavily web-search API (requires ``TAVILY_API_KEY``).
      ``key_reader`` is ignored for this provider.

    Any other value raises :class:`NotImplementedError`.
    """
    provider = (os.environ.get("WEBIQ_PROVIDER") or "webiq").strip().lower()
    if provider == "webiq":
        from dsf.agents.webiq.webiq_sdk import _build_webiq_search

        return _build_webiq_search(client=client, key_reader=key_reader)
    if provider != "tavily":
        raise NotImplementedError(
            f"WEBIQ_PROVIDER {provider!r} not supported "
            "(use 'webiq' (default) or 'tavily')"
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
