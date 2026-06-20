"""Live Application Insights client for :class:`AzureMonitorBackend`.

Builds an async ``mcp_call(spec)`` callable backed by the Application Insights
query API via ``httpx``. Constructed from environment variables but accepts an
injected ``httpx.AsyncClient`` so tests can drive it with ``httpx.MockTransport``
and never touch the network.

The token/key wiring here is the data path; the Azure RBAC role binding that lets
the agent identity read telemetry (Monitoring Reader) is a per-agent seam refined
later (design section 6).
"""

from __future__ import annotations

from typing import TYPE_CHECKING

import httpx

from dsf.agents.mode import env_required

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable

#: Default KQL surfacing recent failed dependencies and exceptions.
_DEFAULT_KQL = (
    "union exceptions, (dependencies | where success == false) "
    "| where timestamp > ago(1h) "
    "| summarize n=count() by cloud_RoleName "
    "| order by n desc"
)


def build_azure_monitor_client_from_env(
    client: httpx.AsyncClient | None = None,
) -> Callable[[dict], Awaitable[list[dict]]]:
    """Return an async ``mcp_call(spec)`` backed by the App Insights query API.

    Env vars:

    * ``AZURE_MONITOR_APP_ID`` (required) — Application Insights application id.
    * ``AZURE_MONITOR_API_KEY`` (required) — API key for the query endpoint.

    ``spec`` may carry an ``app`` (overrides the env app id) and a ``query``
    (overrides the default KQL).
    """
    default_app = env_required("AZURE_MONITOR_APP_ID", hint="App Insights app id")
    api_key = env_required("AZURE_MONITOR_API_KEY", hint="App Insights API key")

    if client is None:
        client = httpx.AsyncClient(
            base_url="https://api.applicationinsights.io",
            headers={"x-api-key": api_key},
            timeout=20.0,
        )

    async def mcp_call(spec: dict) -> list[dict]:
        spec = spec or {}
        app_id = spec.get("app") or default_app
        query = spec.get("query") or _DEFAULT_KQL
        resp = await client.get(
            f"/v1/apps/{app_id}/query",
            params={"query": query},
            headers={"x-api-key": api_key},
        )
        resp.raise_for_status()
        payload = resp.json()
        tables = payload.get("tables") or []
        rows: list[dict] = []
        link = f"https://portal.azure.com/#@/resource/apps/{app_id}/logs"
        for table in tables:
            cols = [c.get("name") for c in table.get("columns", [])]
            for record in table.get("rows", []):
                mapped = dict(zip(cols, record, strict=False))
                role = mapped.get("cloud_RoleName", "telemetry")
                count = mapped.get("n", "")
                rows.append(
                    {
                        "summary": f"{role}: {count} failures/exceptions in the last hour.",
                        "query": query,
                        "link": link,
                        "confidence": 0.7,
                        "product_hints": [],
                    }
                )
        return rows

    return mcp_call


__all__ = ["build_azure_monitor_client_from_env"]
