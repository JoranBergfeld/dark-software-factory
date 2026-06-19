"""Tests for the scheduled sweep (plan Task 5.1)."""

from __future__ import annotations

from dsf.config.flags import triggers_paused
from dsf.container import build_services
from dsf.contracts.enums import RunStatus, SourceKind, TriggerKind
from dsf.triggers.scheduler import PAUSED_MESSAGE, drain_signals, run_sweep, sweep


async def test_sweep_paused_returns_killed_run() -> None:
    services = build_services("local")
    services.config.set_flag("trigger.SCHEDULED.paused", True)
    assert triggers_paused(services.config, TriggerKind.SCHEDULED) is True

    run = await sweep(services)

    assert run.trigger == TriggerKind.SCHEDULED
    assert run.status == RunStatus.KILLED
    messages = " ".join(a.message for a in run.audit)
    assert PAUSED_MESSAGE in messages


async def test_sweep_scopes_to_enabled_source_kinds() -> None:
    services = build_services("local")
    # Disable one agent so we prove sweep filters by agent_enabled.
    services.config.set_flag("agent.TICKETS", False)

    run = await sweep(services)

    assert run.trigger == TriggerKind.SCHEDULED
    assert run.status == RunStatus.OPEN
    assert run.source_kinds  # non-empty
    assert SourceKind.TICKETS not in run.source_kinds
    assert SourceKind.SENTRY in run.source_kinds


async def test_run_sweep_paused_does_not_run_line() -> None:
    services = build_services("local")
    services.config.set_flag("trigger.SCHEDULED.paused", True)

    run = await run_sweep(services)

    assert run.status == RunStatus.KILLED
    assert services.github.calls == []


async def test_run_sweep_runs_line_when_enabled() -> None:
    services = build_services("local")

    run = await run_sweep(services)

    # Scheduled run advanced through the conveyor (terminal, not OPEN).
    assert run.status != RunStatus.OPEN
    # Dry-run default everywhere -> GitHub never called.
    assert services.github.calls == []


async def test_sweep_scopes_run_to_services_product() -> None:
    services = build_services("local")
    services.product = "microbi"
    run = await sweep(services)
    assert run.scope_product_hints == ["microbi"]


async def test_sweep_unscoped_when_no_product() -> None:
    services = build_services("local")
    run = await sweep(services)
    assert run.scope_product_hints == []


async def test_drain_signals_processes_each_buffered_payload() -> None:
    services = build_services("local")
    await services.signals.enqueue({"text": "alpha p99 high", "product_hints": ["alpha"]})
    await services.signals.enqueue({"text": "beta errors", "product_hints": ["beta"]})

    runs = await drain_signals(services)

    # One run per buffered signal, each advanced through the conveyor.
    assert len(runs) == 2
    assert all(r.trigger == TriggerKind.SIGNAL for r in runs)
    assert all(r.status != RunStatus.OPEN for r in runs)
    # Drained: the buffer is empty afterwards.
    assert await services.signals.drain() == []
    # Dry-run posture preserved: no real filing.
    assert services.github.calls == []


async def test_drain_signals_empty_buffer_returns_no_runs() -> None:
    services = build_services("local")
    assert await drain_signals(services) == []


async def test_drain_signals_paused_leaves_items_buffered() -> None:
    services = build_services("local")
    services.config.set_flag("trigger.SIGNAL.paused", True)
    await services.signals.enqueue({"text": "x"})

    runs = await drain_signals(services)

    assert runs == []
    # Items are not dropped while paused: they wait for intake to resume.
    assert await services.signals.drain() == [{"text": "x"}]
