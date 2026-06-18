"""A2A server + auth tests (plan Task 2.0)."""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from dsf.a2a.auth import build_bearer_dependency
from dsf.agents.base import SourceAgent
from dsf.config.store import InMemoryConfigStore
from dsf.contracts.enums import SourceKind
from dsf.contracts.models import EvidenceItem, Provenance
from dsf_testing import RecordingSourceBackend


def _seeded_evidence() -> EvidenceItem:
    return EvidenceItem(
        source_agent="sentry-agent",
        claim="error spike in checkout",
        raw_citation="sentry://issue/42",
        provenance=Provenance(query_used="is:unresolved", source_kind=SourceKind.SENTRY),
        confidence=0.8,
        product_hints=["alpha"],
    )


def _agent(backend: RecordingSourceBackend, cfg: InMemoryConfigStore | None = None) -> SourceAgent:
    return SourceAgent(
        kind=SourceKind.SENTRY,
        backend=backend,
        config=cfg or InMemoryConfigStore.from_defaults(),
        endpoint="http://sentry-agent",
    )


def test_card_endpoint_returns_card():
    agent = _agent(RecordingSourceBackend())
    client = TestClient(agent.make_app(token=""))
    resp = client.get("/card")
    assert resp.status_code == 200
    body = resp.json()
    assert body["kind"] == "SENTRY"
    assert body["name"] == "sentry-agent"
    assert body["enabled"] is True
    assert "gather" in body["capabilities"]


def test_gather_endpoint_returns_evidence():
    backend = RecordingSourceBackend([_seeded_evidence()])
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
    client = TestClient(_agent(RecordingSourceBackend()).make_app(token="s3cret"))
    assert client.get("/card").status_code == 401
    assert client.post("/gather", json={"run_scope": {}}).status_code == 401


def test_blank_bearer_is_401_when_token_configured():
    client = TestClient(_agent(RecordingSourceBackend()).make_app(token="s3cret"))
    resp = client.get("/card", headers={"Authorization": "Bearer "})
    assert resp.status_code == 401


def test_wrong_bearer_is_401_correct_bearer_passes():
    client = TestClient(_agent(RecordingSourceBackend()).make_app(token="s3cret"))
    assert client.get("/card", headers={"Authorization": "Bearer nope"}).status_code == 401
    ok = client.get("/card", headers={"Authorization": "Bearer s3cret"})
    assert ok.status_code == 200


# ---------------------------------------------------------------------------
# Issue #8 -- fail-CLOSED in live mode, constant-time comparison
# ---------------------------------------------------------------------------


def test_build_bearer_dependency_raises_at_startup_when_live_and_no_token():
    """Empty token outside local mode must raise immediately (fail CLOSED)."""
    with pytest.raises(RuntimeError, match="A2A_BEARER_TOKEN"):
        build_bearer_dependency("", mode="live")


def test_build_bearer_dependency_raises_for_any_non_local_mode():
    for bad_mode in ("gh", "azure", "prod", "staging"):
        with pytest.raises(RuntimeError):
            build_bearer_dependency(None, mode=bad_mode)


def test_build_bearer_dependency_open_in_local_mode():
    """Empty token in local mode is fine -- open endpoint."""
    dep = build_bearer_dependency("", mode="local")
    # Dependency must be a no-op (returns None without raising).
    assert dep(None) is None


def test_build_bearer_dependency_constant_time_comparison():
    """Wrong token must be rejected; the correct one must pass.

    The dependency receives the full ``Authorization`` header value
    (e.g. ``"Bearer <token>"``), not a bare token string.
    """
    dep = build_bearer_dependency("supersecret", mode="local")
    from fastapi import HTTPException

    # Prefix-sharing wrong token must be rejected.
    with pytest.raises(HTTPException) as exc_info:
        dep("Bearer supersecrX")
    assert exc_info.value.status_code == 401

    # Exact match passes (returns None, not raises).
    result = dep("Bearer supersecret")
    assert result is None