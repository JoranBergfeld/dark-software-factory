"""Conveyor driver — sequence stations S1..S7 over a run.

``run_line`` drives the seven stations in order, persisting run state to the
blackboard after each station and recording an idempotent checkpoint so a
re-run resumes past completed stations. The line stops early when S1 kills a
duplicate (status KILLED) and converts any per-station exception into a clean
ERROR terminal state (audited, persisted) rather than propagating.
"""

from __future__ import annotations

from collections.abc import Awaitable, Callable
from typing import TYPE_CHECKING

from dsf.contracts.enums import RunStatus
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.stations import (
    s1_triage,
    s2_investigation,
    s3_synthesis,
    s4_grounding,
    s5_council,
    s6_routing,
    s7_filing,
)

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Run

StationFn = Callable[["Run", "Services"], Awaitable["Run"]]

#: Ordered (name, station-fn) pipeline.
STATIONS: list[tuple[str, StationFn]] = [
    (s1_triage.STATION, s1_triage.run),
    (s2_investigation.STATION, s2_investigation.run),
    (s3_synthesis.STATION, s3_synthesis.run),
    (s4_grounding.STATION, s4_grounding.run),
    (s5_council.STATION, s5_council.run),
    (s6_routing.STATION, s6_routing.run),
    (s7_filing.STATION, s7_filing.run),
]


async def run_line(run: Run, services: Services) -> Run:
    """Run the full conveyor over ``run`` and return the final run.

    Persists after every station; on a per-station exception sets status ERROR,
    audits the exception, saves, and stops. A KILLED status (S1 debounce) stops
    the line early without erroring.
    """
    blackboard = Blackboard(services.memory)
    await blackboard.save(run)

    for name, station in STATIONS:
        if await blackboard.is_done(run.id, name):
            continue
        try:
            run = await station(run, services)
        except Exception as exc:  # noqa: BLE001 - terminal ERROR, never propagate
            run.status = RunStatus.ERROR
            run.audit.append(_error_audit(name, exc))
            await blackboard.save(run)
            return run

        await blackboard.save(run)
        await blackboard.checkpoint(run.id, name)

        if run.status == RunStatus.KILLED:
            return run

    return run


def _error_audit(station: str, exc: Exception):
    """Build an audit record describing a station failure."""
    from dsf.contracts.models import AuditRecord

    return AuditRecord(
        station=station,
        message=f"station error ({type(exc).__name__}): {exc}",
    )


__all__ = ["STATIONS", "run_line"]
