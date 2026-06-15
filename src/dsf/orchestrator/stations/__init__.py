"""Conveyor stations S1..S7.

Each station module exposes ``async def run(run: Run, services: Services) -> Run``
and appends at least one :class:`~dsf.contracts.models.AuditRecord` to the run.
Stations read/write the blackboard only through the run object they return; the
conveyor driver owns persistence + checkpointing between stations.
"""

from __future__ import annotations

__all__: list[str] = []
