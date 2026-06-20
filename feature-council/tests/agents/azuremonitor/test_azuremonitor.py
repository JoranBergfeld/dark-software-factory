"""Azure Monitor source agent tests (fixture backend + agent build)."""

from __future__ import annotations

from fastapi.testclient import TestClient

from dsf.agents.azuremonitor.backend import AzureMonitorFixtureBackend
from dsf.agents.azuremonitor.main import app, build_agent
from dsf.contracts.enums import SourceKind


async def test_fixture_backend_returns_grounded_telemetry_evidence():
    backend = AzureMonitorFixtureBackend()
    items = await backend.gather({"product_hints": ["microbi"]})

    assert len(items) >= 1
    for item in items:
        assert item.source_agent == "azuremonitor"
        assert item.raw_citation.strip()
        assert item.provenance.source_kind == SourceKind.AZUREMONITOR
    assert backend.calls == [{"product_hints": ["microbi"]}]


def test_agent_builds_with_azuremonitor_kind():
    agent = build_agent()
    assert agent.kind == SourceKind.AZUREMONITOR
    assert app is not None


def test_card_endpoint_reports_azuremonitor():
    client = TestClient(build_agent().make_app(token=""))
    body = client.get("/card").json()
    assert body["kind"] == "AZUREMONITOR"
    assert body["enabled"] is True
    assert "gather" in body["capabilities"]
