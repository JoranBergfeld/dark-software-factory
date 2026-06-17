"""A2A client tests — in-process transport, degraded paths (plan Task 2.0)."""

from __future__ import annotations

import httpx

from dsf.a2a import client as a2a_client
from dsf.a2a.card import AgentCard
from dsf.agents.base import SourceAgent
from dsf.contracts.enums import SourceKind
from dsf.contracts.models import EvidenceItem, Provenance
from dsf.fakes import FakeConfigStore, FakeSourceBackend


class _RaisingBackend:
    """SourceBackend whose gather always raises."""

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        raise RuntimeError("backend exploded")


def _evidence() -> EvidenceItem:
    return EvidenceItem(
        source_agent="sentry-agent",
        claim="latency regression",
        raw_citation="sentry://issue/7",
        provenance=Provenance(query_used="q", source_kind=SourceKind.SENTRY),
    )


def _transport(agent: SourceAgent) -> httpx.ASGITransport:
    return httpx.ASGITransport(app=agent.make_app(token=""))


async def test_inprocess_gather_returns_evidence():
    agent = SourceAgent(
        kind=SourceKind.SENTRY,
        backend=FakeSourceBackend([_evidence()]),
        config=FakeConfigStore.from_defaults(),
    )
    resp = await a2a_client.gather(
        endpoint=None,
        scope={"product_hints": ["alpha"]},
        transport=_transport(agent),
    )
    assert resp.degraded is False
    assert len(resp.evidence) == 1
    assert resp.evidence[0].raw_citation == "sentry://issue/7"


async def test_inprocess_fetch_card():
    agent = SourceAgent(
        kind=SourceKind.GRAFANA,
        backend=FakeSourceBackend(),
        config=FakeConfigStore.from_defaults(),
    )
    card = await a2a_client.fetch_card(endpoint=None, transport=_transport(agent))
    assert isinstance(card, AgentCard)
    assert card.kind == SourceKind.GRAFANA


async def test_gather_against_raising_backend_degrades_not_raises():
    # SourceAgent already wraps the backend error, so /gather returns a degraded
    # response (HTTP 200) rather than a 500 — the client surfaces it intact.
    agent = SourceAgent(
        kind=SourceKind.SENTRY,
        backend=_RaisingBackend(),
        config=FakeConfigStore.from_defaults(),
    )
    resp = await a2a_client.gather(endpoint=None, scope={}, transport=_transport(agent))
    assert resp.degraded is True
    assert resp.evidence == []
    assert resp.error == "source SENTRY gather failed"


async def test_client_timeout_returns_degraded():
    # Point at an unroutable address with a tiny timeout; the client must
    # swallow the transport/timeout error into a degraded response.
    resp = await a2a_client.gather(
        endpoint="http://10.255.255.1:9/",
        scope={},
        timeout=0.001,
    )
    assert resp.degraded is True
    assert resp.evidence == []
    assert resp.error


async def test_client_http_error_returns_degraded(monkeypatch):
    # A 500 from the agent (raise_for_status) also degrades rather than raising.
    async def _boom(self, run_scope: dict):
        raise RuntimeError("kaboom")

    agent = SourceAgent(
        kind=SourceKind.SENTRY,
        backend=FakeSourceBackend(),
        config=FakeConfigStore.from_defaults(),
    )
    # Bypass SourceAgent wrapping: make the endpoint raise inside FastAPI.
    monkeypatch.setattr(type(agent), "gather", _boom, raising=True)
    resp = await a2a_client.gather(endpoint=None, scope={}, transport=_transport(agent))
    assert resp.degraded is True
    assert resp.error
