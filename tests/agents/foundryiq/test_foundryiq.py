"""FoundryIQ source agent tests (plan Task 2.4)."""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from dsf.agents.foundryiq.backend import FoundryIqFakeBackend, FoundryIqMcpBackend
from dsf.agents.foundryiq.main import app, build_agent
from dsf.contracts.enums import SourceKind
from dsf.fakes import FakeConfigStore


async def test_fake_gather_returns_grounded_evidence():
    backend = FoundryIqFakeBackend()
    out = await backend.gather({"product_hints": ["alpha"]})

    assert len(out) >= 1
    for item in out:
        assert item.source_agent == "foundryiq"
        assert item.raw_citation.strip()
        assert item.provenance.source_kind == SourceKind.FOUNDRYIQ
    # Scope was recorded.
    assert backend.calls == [{"product_hints": ["alpha"]}]


def test_agent_builds_with_foundryiq_kind():
    agent = build_agent()
    assert agent.kind == SourceKind.FOUNDRYIQ
    # Honors an explicitly supplied config store.
    assert build_agent(FakeConfigStore.from_defaults()).kind == SourceKind.FOUNDRYIQ


def test_card_endpoint_reports_foundryiq():
    client = TestClient(build_agent().make_app(token=""))
    resp = client.get("/card")
    assert resp.status_code == 200
    body = resp.json()
    assert body["kind"] == "FOUNDRYIQ"
    assert body["enabled"] is True
    assert "gather" in body["capabilities"]


def test_gather_endpoint_returns_evidence():
    client = TestClient(app)
    resp = client.post("/gather", json={"run_scope": {"product_hints": ["alpha"]}})
    assert resp.status_code == 200
    body = resp.json()
    assert body["degraded"] is False
    assert len(body["evidence"]) >= 1
    first = body["evidence"][0]
    assert first["source_agent"] == "foundryiq"
    assert first["raw_citation"].strip()
    assert first["provenance"]["source_kind"] == "FOUNDRYIQ"


def test_mcp_backend_requires_retrieve_client():
    with pytest.raises(
        RuntimeError,
        match="FoundryIqMcpBackend requires a retrieve client",
    ):
        FoundryIqMcpBackend(retrieve=None)


async def test_mcp_backend_maps_chunks_to_evidence():
    captured: dict[str, str] = {}

    async def fake_retrieve(query: str) -> list[dict]:
        captured["query"] = query
        return [
            {
                "summary": "Roadmap already commits to express checkout in Q3.",
                "doc_ref": "foundryiq://kb/roadmap/alpha-2026-q3",
                "confidence": 0.8,
            },
            {
                "text": "ADR-0142 standardizes the shared retry/backoff library.",
                "url": "https://foundryiq.internal/knowledge/ADR-0142",
            },
        ]

    backend = FoundryIqMcpBackend(retrieve=fake_retrieve)
    out = await backend.gather(
        {"foundryiq_scope": "alpha-kb", "product_hints": ["alpha"]}
    )

    assert len(out) == 2
    assert "alpha-kb" in captured["query"]
    for item in out:
        assert item.source_agent == "foundryiq"
        assert item.raw_citation.strip()
        assert item.provenance.source_kind == SourceKind.FOUNDRYIQ
        assert item.provenance.query_used == captured["query"]
        assert item.product_hints == ["alpha"]
    assert out[0].claim.startswith("Roadmap already commits")
    assert out[0].raw_citation == "foundryiq://kb/roadmap/alpha-2026-q3"
    assert out[1].raw_citation == "https://foundryiq.internal/knowledge/ADR-0142"
