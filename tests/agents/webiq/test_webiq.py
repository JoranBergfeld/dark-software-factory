"""WebIQ source agent tests (plan Task 2.5)."""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from dsf.agents.webiq.backend import WebIqFakeBackend, WebIqMcpBackend
from dsf.agents.webiq.main import app, build_agent
from dsf.contracts.enums import SourceKind


async def test_webiq_fake_returns_grounded_evidence():
    backend = WebIqFakeBackend()
    out = await backend.gather({"product_hints": ["microbi"]})

    assert len(out) >= 1
    assert backend.calls == [{"product_hints": ["microbi"]}]
    for item in out:
        assert item.source_agent == "webiq"
        assert item.raw_citation.strip()
        assert item.raw_citation.startswith("http")
        assert item.provenance.source_kind == SourceKind.WEBIQ


def test_webiq_agent_card_reports_webiq_kind():
    client = TestClient(build_agent().make_app(token=""))
    resp = client.get("/card")
    assert resp.status_code == 200
    assert resp.json()["kind"] == SourceKind.WEBIQ.value


def test_webiq_agent_gather_returns_evidence():
    client = TestClient(build_agent().make_app(token=""))
    resp = client.post("/gather", json={"run_scope": {"product_hints": ["microbi"]}})
    assert resp.status_code == 200
    body = resp.json()
    assert body["degraded"] is False
    assert len(body["evidence"]) >= 1
    assert all(e["source_agent"] == "webiq" for e in body["evidence"])


def test_app_importable():
    assert app is not None


def test_webiq_mcp_requires_search_client():
    with pytest.raises(RuntimeError, match="requires a search client"):
        WebIqMcpBackend(search=None)


async def test_webiq_mcp_maps_results_to_evidence():
    captured: list[str] = []

    async def fake_search(query: str):
        captured.append(query)
        return [
            {
                "finding": "Competitor Acme shipped an AI report builder.",
                "url": "https://www.acme-analytics.com/blog/ai-report-builder",
                "confidence": 0.8,
            },
            {
                "title": "Embedded analytics market demand surges in 2026.",
                "source_url": "https://www.gartner.com/en/documents/ea-2026",
            },
        ]

    backend = WebIqMcpBackend(search=fake_search)
    out = await backend.gather(
        {
            "product_hints": ["microbi"],
            "webiq_query": "microbi competitor feature release 2026",
        }
    )

    assert captured == ["microbi competitor feature release 2026"]
    assert len(out) == 2
    first = out[0]
    assert first.source_agent == "webiq"
    assert first.claim == "Competitor Acme shipped an AI report builder."
    assert first.raw_citation == "https://www.acme-analytics.com/blog/ai-report-builder"
    assert first.provenance.source_kind == SourceKind.WEBIQ
    assert first.provenance.query_used == "microbi competitor feature release 2026"
    assert first.product_hints == ["microbi"]
    # Falls back to title / source_url when finding / url are absent.
    assert out[1].claim == "Embedded analytics market demand surges in 2026."
    assert out[1].raw_citation == "https://www.gartner.com/en/documents/ea-2026"
