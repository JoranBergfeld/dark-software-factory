"""Live Grafana HTTP client for :class:`GrafanaMcpBackend`.

Builds an async ``mcp_call(spec)`` callable backed by the Grafana HTTP API via
``httpx``. Each spec selects an endpoint:

* alerts (default / ``spec["type"] == "alerts"`` / no ``query``) -> the
  Grafana-managed Alertmanager active-alerts endpoint.
* a PromQL ``query`` with a ``datasource_uid`` -> the datasource proxy's
  Prometheus instant-query endpoint.

The client is constructed from environment variables (see
``build_grafana_client_from_env``) but accepts an injected ``httpx.AsyncClient``
so tests can drive it with ``httpx.MockTransport`` and never touch the network.
"""

from __future__ import annotations

from typing import TYPE_CHECKING
from urllib.parse import quote

import httpx

from dsf.agents.mode import env_required

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable


def _render_metric(metric: dict) -> str:
    """Render a Prometheus series ``metric`` dict compactly (``a=b, c=d``)."""
    return ", ".join(f"{k}={v}" for k, v in sorted(metric.items()))


def build_grafana_client_from_env(
    client: httpx.AsyncClient | None = None,
) -> Callable[[dict], Awaitable[list[dict]]]:
    """Return an async ``mcp_call(spec)`` backed by the Grafana HTTP API.

    Env vars:

    * ``GRAFANA_URL`` (required) — base URL of the Grafana instance.
    * ``GRAFANA_TOKEN`` (required) — bearer token for the Grafana API.

    When ``client`` is ``None`` a real ``httpx.AsyncClient`` is constructed with
    the base URL and ``Authorization`` header. When a client is injected (tests
    pass a ``MockTransport``-backed client) the auth header is still applied to
    each request so the contract holds regardless of transport.
    """
    base_url = env_required("GRAFANA_URL", hint="Grafana base URL")
    token = env_required("GRAFANA_TOKEN", hint="Grafana API token")

    if client is None:
        client = httpx.AsyncClient(
            base_url=base_url,
            headers={"Authorization": f"Bearer {token}"},
            timeout=20.0,
        )

    auth_header = f"Bearer {token}"

    async def mcp_call(spec: dict) -> list[dict]:
        spec = spec or {}
        query = spec.get("query")
        datasource_uid = spec.get("datasource_uid")

        if query and datasource_uid:
            url = (
                f"/api/datasources/proxy/uid/{datasource_uid}"
                f"/api/v1/query?query={quote(query, safe='')}"
            )
            resp = await client.get(
                url, headers={"Authorization": auth_header}
            )
            resp.raise_for_status()
            payload = resp.json()
            result = (payload.get("data") or {}).get("result") or []
            rows: list[dict] = []
            for series in result:
                metric = _render_metric(series.get("metric") or {})
                value = (series.get("value") or [None, ""])[1]
                rows.append(
                    {
                        "summary": f"{metric} = {value}",
                        "query": query,
                        "panel_url": "",
                        "confidence": 0.6,
                        "product_hints": [],
                    }
                )
            return rows

        # Default: active Grafana-managed alerts.
        resp = await client.get(
            "/api/alertmanager/grafana/api/v2/alerts"
            "?active=true&silenced=false&inhibited=false",
            headers={"Authorization": auth_header},
        )
        resp.raise_for_status()
        alerts = resp.json()
        rows = []
        for alert in alerts or []:
            labels = alert.get("labels") or {}
            annotations = alert.get("annotations") or {}
            alertname = labels.get("alertname", "")
            detail = (
                annotations.get("summary")
                or annotations.get("description")
                or "firing"
            )
            rows.append(
                {
                    "summary": f"{alertname}: {detail}",
                    "query": alertname,
                    "panel_url": alert.get("generatorURL") or "",
                    "confidence": 0.75,
                    "product_hints": [],
                }
            )
        return rows

    return mcp_call


__all__ = ["build_grafana_client_from_env"]
