"""Live Sentry client + backend-selection tests (no network).

Uses ``httpx.MockTransport`` so the HTTP client is exercised without touching
the network, and ``monkeypatch`` to control mode/env for backend selection.
"""

from __future__ import annotations

import httpx
import pytest

from dsf.agents.sentry.backend import SentryFixtureBackend, SentryMcpBackend
from dsf.agents.sentry.client import build_sentry_client_from_env
from dsf.agents.sentry.main import build_agent

_CANNED_ISSUES = [
    {
        "title": "TypeError: cannot read property 'id'",
        "permalink": "https://acme.sentry.io/issues/4815162342/",
        "count": "1284",
        "userCount": 312,
    },
    {
        "title": "KeyError: tenant_id",
        "permalink": "https://acme.sentry.io/issues/4823390011/",
        "count": "478",
        "userCount": 96,
    },
]


async def test_search_issues_maps_and_hits_project_path():
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["path"] = request.url.path
        captured["auth"] = request.headers.get("Authorization")
        return httpx.Response(200, json=_CANNED_ISSUES)

    mock = httpx.MockTransport(handler)
    client = httpx.AsyncClient(
        transport=mock,
        base_url="https://sentry.io",
        headers={"Authorization": "Bearer t"},
    )
    mcp_call = build_sentry_client_from_env(client=client)

    out = await mcp_call(
        "search_issues",
        organization_slug="acme",
        project_slug="web",
        query="is:unresolved",
    )

    assert "/projects/acme/web/issues/" in captured["path"]
    assert captured["auth"] == "Bearer t"
    assert len(out) == 2
    first = out[0]
    assert first["title"] == "TypeError: cannot read property 'id'"
    assert first["permalink"] == "https://acme.sentry.io/issues/4815162342/"
    assert first["count"] == 1284
    assert isinstance(first["count"], int)
    assert first["user_count"] == 312


async def test_gather_resolves_project_from_product_registry():
    captured: dict = {}

    async def _mcp(tool, **kwargs):
        captured.update(kwargs)
        return []

    backend = SentryMcpBackend(mcp_call=_mcp)
    await backend.gather(
        {
            "product_hints": ["demo"],
            "product_registry": {"sentry_projects": ["proj-demo"]},
        }
    )

    assert captured["project_slug"] == "proj-demo"


async def test_unknown_tool_returns_empty():
    def handler(request: httpx.Request) -> httpx.Response:  # pragma: no cover
        raise AssertionError("should not perform a request for unknown tools")

    mock = httpx.MockTransport(handler)
    client = httpx.AsyncClient(
        transport=mock,
        base_url="https://sentry.io",
        headers={"Authorization": "Bearer t"},
    )
    mcp_call = build_sentry_client_from_env(client=client)
    assert await mcp_call("search_events", query="x") == []


@pytest.fixture(autouse=True)
def _token(monkeypatch):
    monkeypatch.setenv("SENTRY_AUTH_TOKEN", "t")


def test_build_agent_local_uses_fake(monkeypatch):
    monkeypatch.delenv("DSF_MODE", raising=False)
    assert isinstance(build_agent(mode="local").backend, SentryFixtureBackend)


def test_build_agent_default_unset_mode_is_fake(monkeypatch):
    monkeypatch.delenv("DSF_MODE", raising=False)
    assert isinstance(build_agent().backend, SentryFixtureBackend)


def test_build_agent_live_uses_mcp(monkeypatch):
    monkeypatch.setenv("SENTRY_AUTH_TOKEN", "live-token")
    assert isinstance(build_agent(mode="live").backend, SentryMcpBackend)


def test_build_agent_live_missing_token_raises(monkeypatch):
    monkeypatch.delenv("SENTRY_AUTH_TOKEN", raising=False)
    with pytest.raises(RuntimeError):
        build_agent(mode="live")
