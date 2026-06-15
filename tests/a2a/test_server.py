"""A2A server + auth tests (plan Task 2.0)."""

from __future__ import annotations

from fastapi.testclient import TestClient

from dsf.agents.base import SourceAgent
from dsf.contracts.enums import SourceKind
from dsf.contracts.models import EvidenceItem, Provenance
from dsf.fakes import FakeConfigStore, FakeSourceBackend


def _seeded_evidence() -> EvidenceItem:
    return EvidenceItem(
        source_agent="sentry-agent",
        claim="error spike in checkout",
        raw_citation="sentry://issue/42",
        provenance=Provenance(query_used="is:unresolved", source_kind=SourceKind.SENTRY),
        confidence=0.8,
        product_hints=["alpha"],
    )


def _agent(backend: FakeSourceBackend, cfg: FakeConfigStore | None = None) -> SourceAgent:
    return SourceAgent(
        kind=SourceKind.SENTRY,
        backend=backend,
        config=cfg or FakeConfigStore.from_defaults(),
        endpoint="http://sentry-agent",
    )


def test_card_endpoint_returns_card():
    agent = _agent(FakeSourceBackend())
    client = TestClient(agent.make_app(token=""))
    resp = client.get("/card")
    assert resp.status_code == 200
    body = resp.json()
    assert body["kind"] == "SENTRY"
    assert body["name"] == "sentry-agent"
    assert body["enabled"] is True
    assert "gather" in body["capabilities"]


def test_gather_endpoint_returns_evidence():
    backend = FakeSourceBackend([_seeded_evidence()])
    client = TestClient(_agent(backend).make_app(token=""))
    resp = client.post("/gather", json={"run_scope": {"product_hints": ["alpha"]}})
    assert resp.status_code == 200
    body = resp.json()
    assert body["degraded"] is False
    assert len(body["evidence"]) == 1
    assert body["evidence"][0]["raw_citation"] == "sentry://issue/42"
    # Scope was forwarded to the backend.
    assert backend.calls == [{"product_hints": ["alpha"]}]


def test_missing_bearer_is_401_when_token_configured():
    client = TestClient(_agent(FakeSourceBackend()).make_app(token="s3cret"))
    assert client.get("/card").status_code == 401
    assert client.post("/gather", json={"run_scope": {}}).status_code == 401


def test_blank_bearer_is_401_when_token_configured():
    client = TestClient(_agent(FakeSourceBackend()).make_app(token="s3cret"))
    resp = client.get("/card", headers={"Authorization": "Bearer "})
    assert resp.status_code == 401


def test_wrong_bearer_is_401_correct_bearer_passes():
    client = TestClient(_agent(FakeSourceBackend()).make_app(token="s3cret"))
    assert client.get("/card", headers={"Authorization": "Bearer nope"}).status_code == 401
    ok = client.get("/card", headers={"Authorization": "Bearer s3cret"})
    assert ok.status_code == 200
