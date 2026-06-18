"""Tests for S2 investigation — in-process evidence gathering."""

from __future__ import annotations

from dsf.container import build_services
from dsf.contracts.enums import RunStatus, SourceKind, TriggerKind
from dsf.contracts.models import Run
from dsf.orchestrator.stations import s2_investigation


async def test_collects_evidence_from_enabled_agents() -> None:
    services = build_services("local")
    run = Run(
        trigger=TriggerKind.SIGNAL,
        scope_product_hints=["microbi"],
        source_kinds=[SourceKind.SENTRY, SourceKind.GRAFANA],
    )

    result = await s2_investigation.run(run, services)

    assert result.status == RunStatus.INVESTIGATING
    assert len(result.evidence) >= 1
    agents = {e.source_agent for e in result.evidence}
    assert "sentry" in agents
    assert "grafana" in agents


async def test_disabled_agent_contributes_nothing_and_is_audited() -> None:
    services = build_services("local")
    # Disable grafana; sentry stays enabled.
    services.config.set_flag(f"agent.{SourceKind.GRAFANA.value}", False)

    run = Run(
        trigger=TriggerKind.SIGNAL,
        scope_product_hints=["microbi"],
        source_kinds=[SourceKind.SENTRY, SourceKind.GRAFANA],
    )
    result = await s2_investigation.run(run, services)

    agents = {e.source_agent for e in result.evidence}
    assert "grafana" not in agents
    assert "sentry" in agents

    messages = " ".join(a.message for a in result.audit)
    assert "GRAFANA" in messages
    assert "disabled" in messages or "degraded" in messages
