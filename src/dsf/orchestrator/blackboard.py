"""Blackboard — persist/load run state and inter-station artifacts.

The blackboard is a thin facade over the :class:`~dsf.ports.MemoryStore`
working tier. It owns the serialization of the central :class:`Run` object plus
the inter-station artifacts that do not live on the ``Run`` itself
(:class:`Proposal`, :class:`CouncilVerdict`, :class:`RoutedIssue` lists), and a
set of idempotent station checkpoint markers so a re-run of the line skips any
station already completed.

Key conventions (all in the working tier):

* ``run:<id>``        -> the serialized :class:`Run` (``model_dump(mode="json")``)
* ``proposals:<id>``  -> list of serialized :class:`Proposal`
* ``verdicts:<id>``   -> list of serialized :class:`CouncilVerdict`
* ``issues:<id>``     -> list of serialized :class:`RoutedIssue`
* ``done:<id>:<station>`` -> ``True`` when that station finished

Design choice (proposals/verdicts between stations): the ``Run`` contract only
carries proposal *ids* (``run.proposals``), so the full objects are persisted on
the blackboard's working tier rather than mutating the contract. S3 saves the
Proposal objects via :meth:`save_proposals`; later stations reload them via
:meth:`load_proposals`. Verdicts and routed issues follow the same pattern. This
keeps the contracts stable and makes the line fully resumable from memory alone.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.models import (
    AuditRecord,
    CouncilVerdict,
    Proposal,
    RoutedIssue,
    Run,
)

if TYPE_CHECKING:
    from dsf.ports import MemoryStore


def _run_key(run_id: str) -> str:
    return f"run:{run_id}"


def _proposals_key(run_id: str) -> str:
    return f"proposals:{run_id}"


def _verdicts_key(run_id: str) -> str:
    return f"verdicts:{run_id}"


def _issues_key(run_id: str) -> str:
    return f"issues:{run_id}"


def _done_key(run_id: str, station: str) -> str:
    return f"done:{run_id}:{station}"


class Blackboard:
    """Run-state + artifact persistence over a :class:`MemoryStore`."""

    def __init__(self, memory: MemoryStore) -> None:
        self._memory = memory

    @property
    def memory(self) -> MemoryStore:
        """The underlying memory store (working tier)."""
        return self._memory

    # -- Run state -----------------------------------------------------------

    async def save(self, run: Run) -> None:
        """Persist ``run`` to the working tier under ``run:<id>``."""
        await self._memory.put_working(_run_key(run.id), run.model_dump(mode="json"))

    async def load(self, run_id: str) -> Run | None:
        """Load a :class:`Run` by id, or ``None`` if not present."""
        raw = await self._memory.get_working(_run_key(run_id))
        if raw is None:
            return None
        return Run.model_validate(raw)

    # -- Checkpoints (idempotent station markers) ----------------------------

    async def checkpoint(self, run_id: str, station: str) -> None:
        """Mark ``station`` complete for ``run_id`` so a re-run can skip it."""
        await self._memory.put_working(_done_key(run_id, station), True)

    async def is_done(self, run_id: str, station: str) -> bool:
        """Whether ``station`` was already completed for ``run_id``."""
        return bool(await self._memory.get_working(_done_key(run_id, station)))

    # -- Audit helper --------------------------------------------------------

    async def append_audit(self, run: Run, station: str, message: str) -> AuditRecord:
        """Append an :class:`AuditRecord` to ``run`` and persist it.

        Mutates ``run.audit`` in place, saves the run, and returns the record.
        """
        record = AuditRecord(station=station, message=message)
        run.audit.append(record)
        await self.save(run)
        return record

    # -- Proposals -----------------------------------------------------------

    async def save_proposals(self, run_id: str, proposals: list[Proposal]) -> None:
        """Persist the Proposal objects for a run (S3 output)."""
        await self._memory.put_working(
            _proposals_key(run_id),
            [p.model_dump(mode="json") for p in proposals],
        )

    async def load_proposals(self, run_id: str) -> list[Proposal]:
        """Reload the Proposal objects for a run (empty list if none)."""
        raw = await self._memory.get_working(_proposals_key(run_id))
        if not raw:
            return []
        return [Proposal.model_validate(p) for p in raw]

    # -- Verdicts ------------------------------------------------------------

    async def save_verdicts(self, run_id: str, verdicts: list[CouncilVerdict]) -> None:
        """Persist the CouncilVerdict objects for a run (S5 output)."""
        await self._memory.put_working(
            _verdicts_key(run_id),
            [v.model_dump(mode="json") for v in verdicts],
        )

    async def load_verdicts(self, run_id: str) -> list[CouncilVerdict]:
        """Reload the CouncilVerdict objects for a run (empty list if none)."""
        raw = await self._memory.get_working(_verdicts_key(run_id))
        if not raw:
            return []
        return [CouncilVerdict.model_validate(v) for v in raw]

    # -- Routed issues -------------------------------------------------------

    async def save_issues(self, run_id: str, issues: list[RoutedIssue]) -> None:
        """Persist the RoutedIssue objects for a run (S6/S7 output)."""
        await self._memory.put_working(
            _issues_key(run_id),
            [i.model_dump(mode="json") for i in issues],
        )

    async def load_issues(self, run_id: str) -> list[RoutedIssue]:
        """Reload the RoutedIssue objects for a run (empty list if none)."""
        raw = await self._memory.get_working(_issues_key(run_id))
        if not raw:
            return []
        return [RoutedIssue.model_validate(i) for i in raw]


__all__ = ["Blackboard"]
