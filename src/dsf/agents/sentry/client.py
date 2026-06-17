"""Live Sentry MCP client (HTTP) for :class:`SentryMcpBackend`.

Builds an async ``mcp_call(tool_name, **kwargs)`` callable backed by the Sentry
REST API via ``httpx``. The backend only ever calls ``search_issues``; any other
tool name returns an empty list.

The client is constructed from environment variables (see
``build_sentry_client_from_env``) but accepts an injected ``httpx.AsyncClient``
so tests can drive it with ``httpx.MockTransport`` and never touch the network.
"""

from __future__ import annotations

import os
from typing import TYPE_CHECKING

import httpx

from dsf.agents.mode import env_required

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable


def build_sentry_client_from_env(
    client: httpx.AsyncClient | None = None,
) -> Callable[..., Awaitable[list[dict]]]:
    """Return an async ``mcp_call(tool_name, **kwargs)`` backed by the Sentry API.

    Env vars:

    * ``SENTRY_AUTH_TOKEN`` (required) — bearer token for the Sentry API.
    * ``SENTRY_BASE_URL`` (default ``https://sentry.io``).
    * ``SENTRY_ORG`` / ``SENTRY_PROJECT`` — default org/project slugs.

    When ``client`` is ``None`` a real ``httpx.AsyncClient`` is constructed with
    the base URL and ``Authorization`` header. When a client is injected (tests
    pass a ``MockTransport``-backed client) the auth header is still applied to
    each request so the contract holds regardless of transport.
    """
    token = env_required("SENTRY_AUTH_TOKEN", hint="Sentry API auth token")
    base_url = os.environ.get("SENTRY_BASE_URL") or "https://sentry.io"
    default_org = os.environ.get("SENTRY_ORG")
    default_project = os.environ.get("SENTRY_PROJECT")

    if client is None:
        client = httpx.AsyncClient(
            base_url=base_url,
            headers={"Authorization": f"Bearer {token}"},
            timeout=20.0,
        )

    auth_header = f"Bearer {token}"

    async def mcp_call(tool_name: str, **kwargs: object) -> list[dict]:
        if tool_name != "search_issues":
            return []

        org = kwargs.get("organization_slug") or default_org
        project = kwargs.get("project_slug") or default_project
        query = kwargs.get("query", "")

        params = {"query": query, "statsPeriod": "14d", "limit": "25"}
        if project:
            url = f"/api/0/projects/{org}/{project}/issues/"
        else:
            url = f"/api/0/organizations/{org}/issues/"

        resp = await client.get(
            url, params=params, headers={"Authorization": auth_header}
        )
        resp.raise_for_status()

        issues = resp.json()
        result: list[dict] = []
        for issue in issues or []:
            count = issue.get("count", 0)
            result.append(
                {
                    "title": issue.get("title", ""),
                    "permalink": issue.get("permalink", ""),
                    "count": int(count),
                    "user_count": issue.get("userCount", 0),
                    "confidence": 0.75,
                }
            )
        return result

    return mcp_call


__all__ = ["build_sentry_client_from_env"]
