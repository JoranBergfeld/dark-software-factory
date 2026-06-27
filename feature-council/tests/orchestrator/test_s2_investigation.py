"""Tests for S2 investigation — in-process evidence gathering."""

from __future__ import annotations

import pytest

from dsf.contracts.enums import RunStatus, SourceKind, TriggerKind
from dsf.contracts.models import Run
from dsf.orchestrator.stations import s2_investigation
from dsf_testing import build_test_services, config_with_product_record


def _demo_services():
    return build_test_services(
        product="demo",
        config=config_with_product_record(
            "demo",
            github_repo="acme/demo",
            azure_monitor_scope="appinsights-demo",
            grafana_dashboards=["dash-1"],
            foundryiq_scope="kb-demo",
            sentry_projects=["proj-demo"],
        ),
    )


def test_run_scope_injects_product_record():
    services = _demo_services()
    run = Run(
        trigger=TriggerKind.SIGNAL,
        scope_product_hints=["demo"],
        source_kinds=[SourceKind.AZUREMONITOR],
    )
    scope = s2_investigation._run_scope(run, services)
    rs = scope["product_registry"]
    assert rs["azure_monitor_scope"] == "appinsights-demo"
    assert rs["grafana_dashboards"] == ["dash-1"]
    assert rs["foundryiq_scope"] == "kb-demo"
    assert rs["sentry_projects"] == ["proj-demo"]


def test_run_scope_fails_loud_when_record_missing():
    services = build_test_services(product="demo")  # default config, no product.* keys
    run = Run(
        trigger=TriggerKind.SIGNAL,
        scope_product_hints=["demo"],
        source_kinds=[SourceKind.AZUREMONITOR],
    )
    with pytest.raises(ValueError):
        s2_investigation._run_scope(run, services)


def test_live_azuremonitor_backend_resolves_scope_from_run_scope():
    from dsf.agents.azuremonitor.backend import AzureMonitorBackend

    services = _demo_services()
    run = Run(
        trigger=TriggerKind.SIGNAL,
        scope_product_hints=["demo"],
        source_kinds=[SourceKind.AZUREMONITOR],
    )
    scope = s2_investigation._run_scope(run, services)

    async def _dummy_mcp(spec: dict):
        return []

    backend = AzureMonitorBackend(mcp_call=_dummy_mcp)
    assert backend._queries(scope) == [{"app": "appinsights-demo"}]


async def test_collects_evidence_from_enabled_agents() -> None:
    services = build_test_services(
        product="microbi",
        config=config_with_product_record("microbi", github_repo="o/microbi"),
    )
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
    services = build_test_services(
        product="microbi",
        config=config_with_product_record("microbi", github_repo="o/microbi"),
    )
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
