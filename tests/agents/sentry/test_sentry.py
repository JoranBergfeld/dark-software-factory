"""Sentry source agent tests (plan Task 2.2)."""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from dsf.agents.sentry.backend import SentryFakeBackend, SentryMcpBackend
from dsf.agents.sentry.main import app, build_agent
from dsf.contracts.enums import SourceKind


async def test_sentry_fake_returns_grounded_evidence():
    backend = SentryFakeBackend()
    out = await backend.gather({"product_hints": ["microbi"]})

    assert len(out) >= 1
    assert backend.calls == [{"product_hints": ["microbi"]}]
    for item in out:
        assert item.source_agent == "sentry"
        assert item.raw_citation.strip()
        assert item.provenance.source_kind == SourceKind.SENTRY


def test_sentry_agent_card_reports_sentry_kind():
    client = TestClient(build_agent().make_app(token=""))
    resp = client.get("/card")
    assert resp.status_code == 200
    assert resp.json()["kind"] == SourceKind.SENTRY.value


def test_sentry_agent_gather_returns_evidence():
    client = TestClient(build_agent().make_app(token=""))
    resp = client.post("/gather", json={"run_scope": {"product_hints": ["microbi"]}})
    assert resp.status_code == 200
    body = resp.json()
    assert body["degraded"] is False
    assert len(body["evidence"]) >= 1
    assert all(e["source_agent"] == "sentry" for e in body["evidence"])


def test_app_importable():
    assert app is not None


def test_sentry_mcp_requires_client():
    with pytest.raises(RuntimeError, match="requires an mcp_call client"):
        SentryMcpBackend(mcp_call=None)


async def test_sentry_mcp_maps_issues_to_evidence():
    captured: dict = {}

    async def fake_mcp_call(tool_name: str, **kwargs):
        captured["tool_name"] = tool_name
        captured["kwargs"] = kwargs
        return [
            {
                "title": "TypeError: cannot read property 'id'",
                "count": 1284,
                "user_count": 312,
                "permalink": "https://microbi.sentry.io/issues/4815162342/",
                "confidence": 0.9,
            },
            {
                "title": "KeyError: tenant_id",
                "count": 478,
                "user_count": 96,
                "permalink": "https://microbi.sentry.io/issues/4823390011/",
            },
        ]

    backend = SentryMcpBackend(mcp_call=fake_mcp_call)
    out = await backend.gather(
        {
            "product_hints": ["microbi"],
            "organization": "microbi",
            "project": "microbi-web",
            "sentry_query": "is:unresolved is:regression",
        }
    )

    assert captured["tool_name"] == "search_issues"
    assert captured["kwargs"]["query"] == "is:unresolved is:regression"
    assert len(out) == 2
    first = out[0]
    assert first.source_agent == "sentry"
    assert "1284 events" in first.claim
    assert "312 users" in first.claim
    assert first.raw_citation == "https://microbi.sentry.io/issues/4815162342/"
    assert first.provenance.source_kind == SourceKind.SENTRY
    assert first.provenance.query_used == "is:unresolved is:regression"
    assert first.product_hints == ["microbi"]
