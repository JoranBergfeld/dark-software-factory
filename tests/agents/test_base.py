"""SourceAgent base tests (plan Task 2.1)."""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.contracts.enums import SourceKind
from dsf.contracts.models import EvidenceItem, Provenance
from dsf.fakes import FakeConfigStore, FakeSourceBackend


def _evidence() -> EvidenceItem:
    return EvidenceItem(
        source_agent="grafana-agent",
        claim="cpu saturation",
        raw_citation="grafana://panel/3",
        provenance=Provenance(query_used="rate(...)", source_kind=SourceKind.GRAFANA),
    )


async def test_enabled_agent_delegates_to_backend():
    backend = FakeSourceBackend([_evidence()])
    agent = SourceAgent(
        kind=SourceKind.GRAFANA,
        backend=backend,
        config=FakeConfigStore.from_defaults(),
    )
    resp = await agent.gather({"product_hints": ["beta"]})
    assert resp.degraded is False
    assert resp.error is None
    assert len(resp.evidence) == 1
    assert backend.calls == [{"product_hints": ["beta"]}]


async def test_disabled_agent_returns_degraded_empty():
    cfg = FakeConfigStore.from_defaults()
    cfg.set_flag("agent.GRAFANA", False)
    backend = FakeSourceBackend([_evidence()])
    agent = SourceAgent(kind=SourceKind.GRAFANA, backend=backend, config=cfg)

    resp = await agent.gather({})
    assert resp.degraded is True
    assert resp.evidence == []
    assert resp.error == "agent disabled"
    # Backend never invoked when disabled.
    assert backend.calls == []


async def test_backend_exception_degrades():
    class _Boom:
        async def gather(self, run_scope: dict):
            raise ValueError("nope")

    agent = SourceAgent(
        kind=SourceKind.SENTRY,
        backend=_Boom(),
        config=FakeConfigStore.from_defaults(),
    )
    resp = await agent.gather({})
    assert resp.degraded is True
    assert resp.evidence == []
    assert "nope" in (resp.error or "")


def test_card_reflects_enabled_flag():
    cfg = FakeConfigStore.from_defaults()
    agent = SourceAgent(kind=SourceKind.WEBIQ, backend=FakeSourceBackend(), config=cfg)
    assert agent.card().enabled is True
    cfg.set_flag("agent.WEBIQ", False)
    assert agent.card().enabled is False
    assert agent.card().kind == SourceKind.WEBIQ
