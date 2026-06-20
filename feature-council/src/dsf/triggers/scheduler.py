"""Scheduled sweep - the council-owned schedule tick.

The scheduled trigger is the Container Apps cron job of the design (§8). On each
tick the deployed worker runs :func:`run_orchestrator_tick`, which scopes a run
to all currently enabled source kinds and drives it through the conveyor
(:func:`sweep` / :func:`run_sweep`). DSF is pull-only: sweeping source agents is
the system's only automated work source.

If the SCHEDULED trigger is paused (control-center kill switch), the sweep is a
no-op: it returns a KILLED run audited with the pause reason instead of building
a real sweep, so the pause is visible in the audit trail rather than silent.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.config.flags import agent_enabled, triggers_paused
from dsf.contracts.enums import RunStatus, SourceKind, TriggerKind
from dsf.contracts.models import AuditRecord, Run

if TYPE_CHECKING:
    from dsf.container import Services

STATION = "trigger:scheduled"

#: Audit message written when the SCHEDULED trigger is paused.
PAUSED_MESSAGE = "scheduled triggers paused"


def _enabled_source_kinds(services: Services) -> list[SourceKind]:
    """Every source kind whose agent is currently enabled in config."""
    return [kind for kind in SourceKind if agent_enabled(services.config, kind)]


def _paused_run() -> Run:
    """Build a KILLED scheduled run audited with the pause reason."""
    run = Run(trigger=TriggerKind.SCHEDULED, status=RunStatus.KILLED)
    run.audit.append(AuditRecord(station=STATION, message=PAUSED_MESSAGE))
    return run


async def sweep(services: Services) -> Run:
    """Build (do NOT run) a SCHEDULED run scoped to all enabled source kinds.

    Returns a KILLED run if the SCHEDULED trigger is paused. Otherwise returns a
    fresh OPEN run with ``source_kinds`` set to the enabled agents; the caller is
    responsible for driving it through ``run_line``.
    """
    if triggers_paused(services.config, TriggerKind.SCHEDULED):
        return _paused_run()

    source_kinds = _enabled_source_kinds(services)
    scope = [services.product] if services.product else []
    run = Run(
        trigger=TriggerKind.SCHEDULED,
        source_kinds=source_kinds,
        scope_product_hints=scope,
    )
    kinds = ", ".join(k.value for k in source_kinds) or "(none)"
    run.audit.append(AuditRecord(station=STATION, message=f"scheduled sweep: sources=[{kinds}]"))
    return run


async def run_sweep(services: Services) -> Run:
    """Build a scheduled sweep and run it through the conveyor if not paused.

    Returns the KILLED run unchanged when paused; otherwise builds the sweep and
    returns the final run from ``run_line``.
    """
    run = await sweep(services)
    if run.status == RunStatus.KILLED:
        return run
    # Imported lazily to keep the trigger module importable without the full
    # orchestrator graph (and to avoid any import cycle).
    from dsf.orchestrator.conveyor import run_line

    return await run_line(run, services)


async def run_orchestrator_tick(services: Services) -> Run:
    """One council-owned tick: run the source sweep.

    Returns the SCHEDULED sweep run. DSF is pull-only, so this is the unit the
    deployed orchestrator worker runs per schedule tick.
    """
    return await run_sweep(services)


__all__ = [
    "PAUSED_MESSAGE",
    "STATION",
    "run_orchestrator_tick",
    "run_sweep",
    "sweep",
]
