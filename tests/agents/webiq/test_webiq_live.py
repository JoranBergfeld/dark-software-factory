"""Live WebIQ client + backend-selection tests (no network).

Uses ``httpx.MockTransport`` so the HTTP client is exercised without touching
the network, and ``monkeypatch`` to control mode/env for backend selection.
"""

from __future__ import annotations

import httpx
import pytest

from dsf.agents.webiq.backend import WebIqFixtureBackend, WebIqMcpBackend
from dsf.agents.webiq.client import build_webiq_client_from_env
from dsf.agents.webiq.main import build_agent

_CANNED_TAVILY = {
    "results": [
        {
            "title": "X",
            "url": "https://ex.com/a",
            "content": "finding text",
            "score": 0.81,
        }
    ]
}


@pytest.fixture(autouse=True)
def _api_key(monkeypatch):
    monkeypatch.setenv("TAVILY_API_KEY", "tv-key")
    monkeypatch.delenv("WEBIQ_PROVIDER", raising=False)


async def test_search_maps_results_and_hits_tavily():
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["host"] = request.url.host
        captured["path"] = request.url.path
        captured["method"] = request.method
        return httpx.Response(200, json=_CANNED_TAVILY)

    mock = httpx.MockTransport(handler)
    client = httpx.AsyncClient(transport=mock)
    search = build_webiq_client_from_env(client=client)

    out = await search("microbi competitor release")

    assert captured["method"] == "POST"
    assert captured["host"] == "api.tavily.com"
    assert captured["path"] == "/search"
    assert len(out) == 1
    result = out[0]
    assert result["finding"] == "finding text"
    assert result["url"] == "https://ex.com/a"
    assert result["confidence"] == 0.81


def test_build_agent_local_uses_fake(monkeypatch):
    monkeypatch.delenv("DSF_MODE", raising=False)
    assert isinstance(build_agent(mode="local").backend, WebIqFixtureBackend)


def test_build_agent_default_unset_mode_is_fake(monkeypatch):
    monkeypatch.delenv("DSF_MODE", raising=False)
    assert isinstance(build_agent().backend, WebIqFixtureBackend)


def test_build_agent_live_uses_mcp(monkeypatch):
    monkeypatch.setenv("TAVILY_API_KEY", "live-key")
    assert isinstance(build_agent(mode="live").backend, WebIqMcpBackend)


def test_build_agent_live_missing_key_raises(monkeypatch):
    monkeypatch.delenv("TAVILY_API_KEY", raising=False)
    with pytest.raises(RuntimeError):
        build_agent(mode="live")


def test_build_client_unsupported_provider_raises(monkeypatch):
    monkeypatch.setenv("WEBIQ_PROVIDER", "bing")
    with pytest.raises(NotImplementedError):
        build_webiq_client_from_env()
