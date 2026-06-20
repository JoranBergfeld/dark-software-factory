"""Live FoundryIQ client + backend-selection tests (no network).

Uses ``httpx.MockTransport`` so the HTTP client is exercised without touching
the network, and ``monkeypatch`` to control mode/env for backend selection.
"""

from __future__ import annotations

import httpx
import pytest

from dsf.agents.foundryiq.backend import FoundryIqFixtureBackend, FoundryIqMcpBackend
from dsf.agents.foundryiq.client import build_foundryiq_client_from_env
from dsf.agents.foundryiq.main import build_agent

_CANNED = {
    "value": [
        {
            "content": "Auth roadmap commits to passkeys in Q4.",
            "url": "https://kb/doc1",
            "@search.score": 3.2,
        }
    ]
}


@pytest.fixture(autouse=True)
def _search_env(monkeypatch):
    monkeypatch.setenv("AZURE_SEARCH_ENDPOINT", "https://svc.search.windows.net")
    monkeypatch.setenv("AZURE_SEARCH_INDEX", "knowledge")
    monkeypatch.setenv("AZURE_SEARCH_KEY", "k")
    monkeypatch.delenv("AZURE_SEARCH_CONTENT_FIELD", raising=False)
    monkeypatch.delenv("AZURE_SEARCH_REF_FIELD", raising=False)


def _client(handler) -> httpx.AsyncClient:
    return httpx.AsyncClient(
        transport=httpx.MockTransport(handler),
        base_url="https://svc.search.windows.net",
        headers={"api-key": "k", "Content-Type": "application/json"},
    )


async def test_retrieve_maps_chunks_and_hits_search_path():
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["path"] = request.url.path
        captured["api_key"] = request.headers.get("api-key")
        return httpx.Response(200, json=_CANNED)

    retrieve = build_foundryiq_client_from_env(client=_client(handler))
    out = await retrieve("auth roadmap")

    assert "/indexes/knowledge/docs/search" in captured["path"]
    assert captured["api_key"] == "k"
    assert len(out) == 1
    chunk = out[0]
    assert chunk["summary"] == "Auth roadmap commits to passkeys in Q4."
    assert chunk["doc_ref"] == "https://kb/doc1"
    assert 0.0 < chunk["confidence"] < 1.0


async def test_retrieve_respects_custom_content_and_ref_fields(monkeypatch):
    monkeypatch.setenv("AZURE_SEARCH_CONTENT_FIELD", "text")
    monkeypatch.setenv("AZURE_SEARCH_REF_FIELD", "doc_link")

    canned = {
        "value": [
            {
                "text": "ADR-9 standardizes retries.",
                "doc_link": "https://kb/adr-9",
                "@search.score": 1.0,
            }
        ]
    }

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json=canned)

    retrieve = build_foundryiq_client_from_env(client=_client(handler))
    out = await retrieve("retries")

    assert out[0]["summary"] == "ADR-9 standardizes retries."
    assert out[0]["doc_ref"] == "https://kb/adr-9"
    assert out[0]["confidence"] == pytest.approx(0.5)


def test_build_query_reads_foundryiq_scope_from_product_registry():
    async def _retrieve(query):
        return []

    backend = FoundryIqMcpBackend(retrieve=_retrieve)
    scope = {
        "product_registry": {"foundryiq_scope": "kb-demo"},
        "product_hints": ["demo"],
    }

    assert "kb-demo" in backend._build_query(scope)


def test_build_agent_local_uses_fake(monkeypatch):
    monkeypatch.delenv("DSF_MODE", raising=False)
    assert isinstance(build_agent(mode="local").backend, FoundryIqFixtureBackend)


def test_build_agent_default_unset_mode_is_fake(monkeypatch):
    monkeypatch.delenv("DSF_MODE", raising=False)
    assert isinstance(build_agent().backend, FoundryIqFixtureBackend)


def test_build_agent_live_uses_mcp(monkeypatch):
    assert isinstance(build_agent(mode="live").backend, FoundryIqMcpBackend)


@pytest.mark.parametrize(
    "missing",
    ["AZURE_SEARCH_ENDPOINT", "AZURE_SEARCH_INDEX", "AZURE_SEARCH_KEY"],
)
def test_build_agent_live_missing_env_raises(monkeypatch, missing):
    monkeypatch.delenv(missing, raising=False)
    with pytest.raises(RuntimeError):
        build_agent(mode="live")
