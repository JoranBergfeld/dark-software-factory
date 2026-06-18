"""Live Grafana client + backend-selection tests (no network).

Uses ``httpx.MockTransport`` so the HTTP client is exercised without touching
the network, and ``monkeypatch`` to control mode/env for backend selection.
"""

from __future__ import annotations

import httpx
import pytest

from dsf.agents.grafana.backend import GrafanaFakeBackend, GrafanaMcpBackend
from dsf.agents.grafana.client import build_grafana_client_from_env
from dsf.agents.grafana.main import build_agent

_CANNED_ALERTS = [
    {
        "labels": {"alertname": "HighErrorRate", "severity": "critical"},
        "annotations": {"summary": "Error rate above 5% on api gateway"},
        "generatorURL": "https://grafana.example.com/d/gw/gateway?viewPanel=3",
    },
    {
        "labels": {"alertname": "LatencyP99"},
        "annotations": {"description": "p99 latency exceeds 1.2s"},
        "generatorURL": "",
    },
]

_CANNED_VECTOR = {
    "status": "success",
    "data": {
        "resultType": "vector",
        "result": [
            {
                "metric": {"__name__": "up", "instance": "node-1", "job": "api"},
                "value": [1718000000, "1"],
            },
            {
                "metric": {"__name__": "up", "instance": "node-2", "job": "api"},
                "value": [1718000000, "0"],
            },
        ],
    },
}


@pytest.fixture(autouse=True)
def _env(monkeypatch):
    monkeypatch.setenv("GRAFANA_URL", "https://grafana.example.com")
    monkeypatch.setenv("GRAFANA_TOKEN", "tok")


async def test_alerts_maps_and_hits_alertmanager_path():
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["path"] = request.url.path
        captured["auth"] = request.headers.get("Authorization")
        return httpx.Response(200, json=_CANNED_ALERTS)

    mock = httpx.MockTransport(handler)
    client = httpx.AsyncClient(
        transport=mock,
        base_url="https://grafana.example.com",
        headers={"Authorization": "Bearer tok"},
    )
    mcp_call = build_grafana_client_from_env(client=client)

    rows = await mcp_call({"type": "alerts"})

    assert captured["path"] == "/api/alertmanager/grafana/api/v2/alerts"
    assert captured["auth"] == "Bearer tok"
    assert len(rows) == 2
    first = rows[0]
    assert first["summary"] == "HighErrorRate: Error rate above 5% on api gateway"
    assert first["query"] == "HighErrorRate"
    assert (
        first["panel_url"]
        == "https://grafana.example.com/d/gw/gateway?viewPanel=3"
    )
    assert first["confidence"] == 0.75
    # Falls back to description when summary is absent.
    assert rows[1]["summary"] == "LatencyP99: p99 latency exceeds 1.2s"
    assert rows[1]["panel_url"] == ""


async def test_empty_spec_defaults_to_alerts():
    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.path == "/api/alertmanager/grafana/api/v2/alerts"
        return httpx.Response(200, json=_CANNED_ALERTS)

    mock = httpx.MockTransport(handler)
    client = httpx.AsyncClient(
        transport=mock,
        base_url="https://grafana.example.com",
        headers={"Authorization": "Bearer tok"},
    )
    mcp_call = build_grafana_client_from_env(client=client)
    rows = await mcp_call({})
    assert len(rows) == 2


async def test_promql_query_maps_and_hits_proxy_path():
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["path"] = request.url.path
        captured["query"] = request.url.query.decode()
        captured["auth"] = request.headers.get("Authorization")
        return httpx.Response(200, json=_CANNED_VECTOR)

    mock = httpx.MockTransport(handler)
    client = httpx.AsyncClient(
        transport=mock,
        base_url="https://grafana.example.com",
        headers={"Authorization": "Bearer tok"},
    )
    mcp_call = build_grafana_client_from_env(client=client)

    rows = await mcp_call({"query": "up", "datasource_uid": "prom"})

    assert "/api/datasources/proxy/uid/prom/api/v1/query" in captured["path"]
    assert "query=up" in captured["query"]
    assert captured["auth"] == "Bearer tok"
    assert len(rows) == 2
    first = rows[0]
    assert first["query"] == "up"
    assert first["panel_url"] == ""
    assert first["confidence"] == 0.6
    # value rendered into the summary
    assert "= 1" in first["summary"]
    assert "instance=node-1" in first["summary"]
    assert "= 0" in rows[1]["summary"]


def test_build_agent_local_uses_fake(monkeypatch):
    monkeypatch.delenv("DSF_MODE", raising=False)
    assert isinstance(build_agent(mode="local").backend, GrafanaFakeBackend)


def test_build_agent_default_unset_mode_is_fake(monkeypatch):
    monkeypatch.delenv("DSF_MODE", raising=False)
    assert isinstance(build_agent().backend, GrafanaFakeBackend)


def test_build_agent_live_uses_mcp(monkeypatch):
    monkeypatch.setenv("GRAFANA_URL", "https://grafana.example.com")
    monkeypatch.setenv("GRAFANA_TOKEN", "live-token")
    assert isinstance(build_agent(mode="live").backend, GrafanaMcpBackend)


def test_build_agent_live_missing_env_raises(monkeypatch):
    monkeypatch.delenv("GRAFANA_URL", raising=False)
    monkeypatch.delenv("GRAFANA_TOKEN", raising=False)
    with pytest.raises(RuntimeError):
        build_agent(mode="live")
