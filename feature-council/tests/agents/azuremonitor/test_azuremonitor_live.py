"""Live App Insights client + backend selection (no network)."""

from __future__ import annotations

import httpx
import pytest

from dsf.agents.azuremonitor.backend import (
    AzureMonitorBackend,
    AzureMonitorFixtureBackend,
)
from dsf.agents.azuremonitor.client import build_azure_monitor_client_from_env
from dsf.agents.azuremonitor.main import build_agent
from dsf.contracts.enums import SourceKind

_CANNED = {
    "tables": [
        {
            "name": "PrimaryResult",
            "columns": [{"name": "cloud_RoleName"}, {"name": "n"}],
            "rows": [["api", 42], ["checkout", 17]],
        }
    ]
}


@pytest.fixture(autouse=True)
def _env(monkeypatch):
    monkeypatch.setenv("AZURE_MONITOR_APP_ID", "app-123")
    monkeypatch.setenv("AZURE_MONITOR_API_KEY", "key")


def test_backend_requires_mcp_call():
    with pytest.raises(RuntimeError, match="requires an mcp_call client"):
        AzureMonitorBackend(mcp_call=None)


async def test_client_maps_rows_and_hits_query_path():
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["path"] = request.url.path
        captured["key"] = request.headers.get("x-api-key")
        return httpx.Response(200, json=_CANNED)

    client = httpx.AsyncClient(
        transport=httpx.MockTransport(handler),
        base_url="https://api.applicationinsights.io",
        headers={"x-api-key": "key"},
    )
    mcp_call = build_azure_monitor_client_from_env(client=client)
    rows = await mcp_call({"app": "app-123"})

    assert captured["path"] == "/v1/apps/app-123/query"
    assert captured["key"] == "key"
    assert len(rows) == 2
    assert rows[0]["summary"].startswith("api: 42")
    assert rows[0]["confidence"] == 0.7


async def test_backend_maps_client_rows_to_evidence():
    async def fake_mcp_call(spec: dict) -> list[dict]:
        return [
            {
                "summary": "api: 42 failures/exceptions in the last hour.",
                "query": "exceptions | summarize count()",
                "link": "https://portal.azure.com/x",
                "confidence": 0.7,
                "product_hints": ["microbi", "api"],
            }
        ]

    backend = AzureMonitorBackend(mcp_call=fake_mcp_call)
    items = await backend.gather({"azure_monitor_scope": "app-123"})

    assert len(items) == 1
    item = items[0]
    assert item.source_agent == "azuremonitor"
    assert item.provenance.source_kind == SourceKind.AZUREMONITOR
    assert item.confidence == pytest.approx(0.7)
    assert item.product_hints == ["microbi", "api"]


async def test_backend_without_scope_yields_no_evidence():
    async def fake_mcp_call(spec: dict) -> list[dict]:
        raise AssertionError("must not be called without a scope")

    backend = AzureMonitorBackend(mcp_call=fake_mcp_call)
    assert await backend.gather({}) == []


def test_build_agent_local_uses_fixture(monkeypatch):
    monkeypatch.delenv("DSF_MODE", raising=False)
    assert isinstance(build_agent(mode="local").backend, AzureMonitorFixtureBackend)


def test_build_agent_live_uses_real_backend(monkeypatch):
    monkeypatch.setenv("AZURE_MONITOR_APP_ID", "app-123")
    monkeypatch.setenv("AZURE_MONITOR_API_KEY", "key")
    assert isinstance(build_agent(mode="live").backend, AzureMonitorBackend)


def test_build_agent_live_missing_env_raises(monkeypatch):
    monkeypatch.delenv("AZURE_MONITOR_APP_ID", raising=False)
    monkeypatch.delenv("AZURE_MONITOR_API_KEY", raising=False)
    with pytest.raises(RuntimeError):
        build_agent(mode="live")
