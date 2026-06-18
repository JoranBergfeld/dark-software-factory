"""SourceAgent base tests (plan Task 2.1)."""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.config.store import InMemoryConfigStore
from dsf.contracts.enums import SourceKind
from dsf.contracts.models import EvidenceItem, Provenance
from tests.support.source_double import RecordingSourceBackend


def _evidence() -> EvidenceItem:
    return EvidenceItem(
        source_agent="grafana-agent",
        claim="cpu saturation",
        raw_citation="grafana://panel/3",
        provenance=Provenance(query_used="rate(...)", source_kind=SourceKind.GRAFANA),
    )


async def test_enabled_agent_delegates_to_backend():
    backend = RecordingSourceBackend([_evidence()])
    agent = SourceAgent(
        kind=SourceKind.GRAFANA,
        backend=backend,
        config=InMemoryConfigStore.from_defaults(),
    )
    resp = await agent.gather({"product_hints": ["beta"]})
    assert resp.degraded is False
    assert resp.error is None
    assert len(resp.evidence) == 1
    assert backend.calls == [{"product_hints": ["beta"]}]


async def test_disabled_agent_returns_degraded_empty():
    cfg = InMemoryConfigStore.from_defaults()
    cfg.set_flag("agent.GRAFANA", False)
    backend = RecordingSourceBackend([_evidence()])
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
        config=InMemoryConfigStore.from_defaults(),
    )
    resp = await agent.gather({})
    assert resp.degraded is True
    assert resp.evidence == []
    assert resp.error == "source SENTRY gather failed"


def test_card_reflects_enabled_flag():
    cfg = InMemoryConfigStore.from_defaults()
    agent = SourceAgent(kind=SourceKind.WEBIQ, backend=RecordingSourceBackend(), config=cfg)
    assert agent.card().enabled is True
    cfg.set_flag("agent.WEBIQ", False)
    assert agent.card().enabled is False
    assert agent.card().kind == SourceKind.WEBIQ


async def test_backend_exception_does_not_leak_sensitive_info():
    """Error details (e.g. URLs) must NOT reach the A2AResponse.error field."""
    class _BoomWithURL:
        async def gather(self, run_scope: dict):
            raise RuntimeError("MCP call via http://secret.internal:8403/mcp failed: boom")

    agent = SourceAgent(
        kind=SourceKind.SENTRY,
        backend=_BoomWithURL(),
        config=InMemoryConfigStore.from_defaults(),
    )
    resp = await agent.gather({})
    assert resp.degraded is True
    assert resp.evidence == []
    # The raw exception message must not appear in the audit-facing error string
    assert "http://secret.internal" not in (resp.error or "")
    assert resp.error == "source SENTRY gather failed"


async def test_keyboard_interrupt_propagates_through_gather():
    """KeyboardInterrupt is NOT an Exception; it must bypass the degrade handler."""
    class _KI:
        async def gather(self, run_scope: dict):
            raise KeyboardInterrupt

    agent = SourceAgent(
        kind=SourceKind.SENTRY,
        backend=_KI(),
        config=InMemoryConfigStore.from_defaults(),
    )
    import pytest
    with pytest.raises(KeyboardInterrupt):
        await agent.gather({})
