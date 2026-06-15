"""Grafana source agent tests (plan Task 2.3)."""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from dsf.agents.grafana.backend import GrafanaFakeBackend, GrafanaMcpBackend
from dsf.agents.grafana.main import app, build_agent
from dsf.contracts.enums import SourceKind


async def test_fake_backend_returns_grounded_evidence():
    backend = GrafanaFakeBackend()
    items = await backend.gather({"product_hints": ["homelab-dash"]})

    assert len(items) >= 1
    for item in items:
        assert item.source_agent == "grafana"
        assert item.raw_citation.strip()
        assert item.provenance.source_kind == SourceKind.GRAFANA
    # Scope was recorded.
    assert backend.calls == [{"product_hints": ["homelab-dash"]}]


def test_agent_builds_with_grafana_kind():
    agent = build_agent()
    assert agent.kind == SourceKind.GRAFANA
    assert app is not None


def test_card_endpoint_reports_grafana():
    client = TestClient(build_agent().make_app(token=""))
    resp = client.get("/card")
    assert resp.status_code == 200
    body = resp.json()
    assert body["kind"] == "GRAFANA"
    assert body["enabled"] is True
    assert "gather" in body["capabilities"]


def test_gather_endpoint_returns_evidence():
    client = TestClient(build_agent().make_app(token=""))
    resp = client.post("/gather", json={"run_scope": {"product_hints": ["homelab-dash"]}})
    assert resp.status_code == 200
    body = resp.json()
    assert body["degraded"] is False
    assert len(body["evidence"]) >= 1
    first = body["evidence"][0]
    assert first["source_agent"] == "grafana"
    assert first["raw_citation"].strip()
    assert first["provenance"]["source_kind"] == "GRAFANA"


def test_mcp_backend_requires_mcp_call():
    with pytest.raises(
        RuntimeError, match="GrafanaMcpBackend requires an mcp_call client"
    ):
        GrafanaMcpBackend(mcp_call=None)


async def test_mcp_backend_maps_results_to_evidence():
    async def fake_mcp_call(spec: dict) -> list[dict]:
        return [
            {
                "summary": "Error-rate jumped to 7% on the api gateway over 10m.",
                "panel_url": "https://grafana.homelab.lan/d/gw/gateway?viewPanel=3",
                "query": 'sum(rate(http_requests_total{code=~"5.."}[5m]))',
                "confidence": 0.77,
                "product_hints": ["homelab-dash", "gateway"],
            }
        ]

    backend = GrafanaMcpBackend(mcp_call=fake_mcp_call)
    items = await backend.gather({"grafana_queries": [{"query": "ignored"}]})

    assert len(items) == 1
    item = items[0]
    assert item.source_agent == "grafana"
    assert item.claim.startswith("Error-rate jumped")
    assert item.raw_citation == "https://grafana.homelab.lan/d/gw/gateway?viewPanel=3"
    assert item.provenance.source_kind == SourceKind.GRAFANA
    assert item.provenance.query_used.startswith("sum(rate(")
    assert item.confidence == pytest.approx(0.77)
    assert item.product_hints == ["homelab-dash", "gateway"]
