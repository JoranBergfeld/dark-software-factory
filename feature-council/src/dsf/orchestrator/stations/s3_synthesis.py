"""S3 — Synthesis (agentic).

Cluster the run's evidence into candidate :class:`Proposal`s via the council
synthesizer. The ``Run`` contract only carries proposal *ids* (``run.proposals``),
so the full Proposal objects are persisted on the blackboard's working tier
under ``proposals:<run_id>``; downstream stations reload them with
:meth:`Blackboard.load_proposals`. :func:`load_proposals` re-exports that loader
for convenience.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.enums import RunStatus
from dsf.council.charter_context import load_charter
from dsf.council.synthesizer import synthesize
from dsf.observability.tracing import span_attrs_for_run
from dsf.orchestrator.blackboard import Blackboard

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run

STATION = "S3:synthesis"

#: Tag applied to proposals whose product has no active charter.
UNCHARTED_TAG = "uncharted product context"


async def load_proposals(run_id: str, services: Services) -> list[Proposal]:
    """Reload the Proposal objects synthesized for ``run_id`` (helper)."""
    return await Blackboard(services.memory).load_proposals(run_id)


async def run(run: Run, services: Services) -> Run:
    """Synthesize proposals from evidence and persist them to the blackboard."""
    with services.tracer.span("s3_synthesis", **span_attrs_for_run(run)):
        run.status = RunStatus.SYNTHESIZING

        proposals = await synthesize(run, services)
        await _tag_uncharted(run, services, proposals)

        blackboard = Blackboard(services.memory)
        await blackboard.save_proposals(run.id, proposals)
        run.proposals = [p.id for p in proposals]

        run.audit.append(
            _audit(
                f"synthesized {len(proposals)} proposal(s) "
                f"from {len(run.evidence)} evidence item(s)"
            )
        )
        return run


async def _tag_uncharted(run: Run, services: Services, proposals: list[Proposal]) -> None:
    """Tag each proposal whose product has no active charter (uncharted context)."""
    for proposal in proposals:
        if proposal.product is None:
            continue
        charter = await load_charter(services, run, proposal.product)
        if charter is None:
            proposal.context_tags.append(UNCHARTED_TAG)


def _audit(message: str):
    """Construct an audit record for this station."""
    from dsf.contracts.models import AuditRecord

    return AuditRecord(station=STATION, message=message)


__all__ = ["STATION", "load_proposals", "run"]
