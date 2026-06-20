"""Tests for S2 investigation — in-process evidence gathering."""

from __future__ import annotations

from dsf.config.registry import Product
from dsf.contracts.enums import RunStatus, SourceKind, TriggerKind
from dsf.contracts.models import Run
from dsf.orchestrator.stations import s2_investigation
from dsf_testing import build_test_services


def _demo_registry() -> dict[str, Product]:
    return {
        "demo": Product(
            key="demo",
            github_repo="acme/demo",
            azure_monitor_scope="appinsights-demo",
            grafana_dashboards=["dash-1"],
            foundryiq_scope="kb-demo",
            sentry_projects=["proj-demo"],
        )
    }


def test_run_scope_injects_resolved_product_registry(monkeypatch) -> None:
    monkeypatch.setattr(
        s2_investigation, "load_registry", lambda: _demo_registry(), raising=False
    )
    run = Run(
        trigger=TriggerKind.SIGNAL,
        scope_product_hints=["demo"],
        source_kinds=[SourceKind.AZUREMONITOR],
    )

    scope = s2_investigation._run_scope(run)

    registry_scope = scope.get("product_registry", {})
    assert registry_scope.get("azure_monitor_scope") == "appinsights-demo"
    assert registry_scope.get("grafana_dashboards") == ["dash-1"]
    assert registry_scope.get("foundryiq_scope") == "kb-demo"
    assert registry_scope.get("sentry_projects") == ["proj-demo"]


def test_run_scope_omits_registry_when_no_product_matches(monkeypatch) -> None:
    monkeypatch.setattr(
        s2_investigation, "load_registry", lambda: _demo_registry(), raising=False
    )
    run = Run(
        trigger=TriggerKind.SIGNAL,
        scope_product_hints=["no-such-product"],
        source_kinds=[SourceKind.AZUREMONITOR],
    )

    scope = s2_investigation._run_scope(run)

    assert "product_registry" not in scope


def test_live_azuremonitor_backend_resolves_scope_from_run_scope(monkeypatch) -> None:
    from dsf.agents.azuremonitor.backend import AzureMonitorBackend

    monkeypatch.setattr(
        s2_investigation, "load_registry", lambda: _demo_registry(), raising=False
    )
    run = Run(
        trigger=TriggerKind.SIGNAL,
        scope_product_hints=["demo"],
        source_kinds=[SourceKind.AZUREMONITOR],
    )
    scope = s2_investigation._run_scope(run)

    async def _dummy_mcp(spec: dict):
        return []

    backend = AzureMonitorBackend(mcp_call=_dummy_mcp)
    assert backend._queries(scope) == [{"app": "appinsights-demo"}]


async def test_collects_evidence_from_enabled_agents() -> None:
    services = build_test_services()
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
    services = build_test_services()
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
