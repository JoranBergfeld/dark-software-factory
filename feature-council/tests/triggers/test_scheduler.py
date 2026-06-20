"""Tests for the scheduled sweep (plan Task 5.1)."""

from __future__ import annotations

from dsf.config.flags import triggers_paused
from dsf.contracts.enums import RunStatus, SourceKind, TriggerKind
from dsf.triggers.scheduler import (
    PAUSED_MESSAGE,
    run_orchestrator_tick,
    run_sweep,
    sweep,
)
from dsf_testing import build_test_services


async def test_sweep_paused_returns_killed_run() -> None:
    services = build_test_services()
    services.config.set_flag("trigger.SCHEDULED.paused", True)
    assert triggers_paused(services.config, TriggerKind.SCHEDULED) is True

    run = await sweep(services)

    assert run.trigger == TriggerKind.SCHEDULED
    assert run.status == RunStatus.KILLED
    messages = " ".join(a.message for a in run.audit)
    assert PAUSED_MESSAGE in messages


async def test_sweep_scopes_to_enabled_source_kinds() -> None:
    services = build_test_services()
    # Disable one agent so we prove sweep filters by agent_enabled.
    services.config.set_flag("agent.WEBIQ", False)

    run = await sweep(services)

    assert run.trigger == TriggerKind.SCHEDULED
    assert run.status == RunStatus.OPEN
    assert run.source_kinds  # non-empty
    assert SourceKind.WEBIQ not in run.source_kinds
    assert SourceKind.SENTRY in run.source_kinds


async def test_run_sweep_paused_does_not_run_line() -> None:
    services = build_test_services()
    services.config.set_flag("trigger.SCHEDULED.paused", True)

    run = await run_sweep(services)

    assert run.status == RunStatus.KILLED
    assert services.github.calls == []


async def test_run_sweep_runs_line_when_enabled() -> None:
    services = build_test_services()

    run = await run_sweep(services)

    # Scheduled run advanced through the conveyor (terminal, not OPEN).
    assert run.status != RunStatus.OPEN
    # Dry-run default everywhere -> GitHub never called.
    assert services.github.calls == []


async def test_sweep_scopes_run_to_services_product() -> None:
    services = build_test_services()
    services.product = "microbi"
    run = await sweep(services)
    assert run.scope_product_hints == ["microbi"]


async def test_sweep_unscoped_when_no_product() -> None:
    services = build_test_services()
    run = await sweep(services)
    assert run.scope_product_hints == []


async def test_orchestrator_tick_sweeps() -> None:
    services = build_test_services()

    swept = await run_orchestrator_tick(services)

    assert swept.trigger == TriggerKind.SCHEDULED
    assert swept.status != RunStatus.OPEN
