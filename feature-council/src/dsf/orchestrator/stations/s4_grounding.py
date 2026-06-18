"""S4 — Grounding Verification (hard gate).

For every proposal, strip ``evidence_ids`` that do not trace to a real evidence
item on the run. A proposal left with no grounded evidence is killed (dropped),
with an audit record. Survivors are re-persisted to the blackboard.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.enums import RunStatus
from dsf.observability.tracing import span_attrs_for_run
from dsf.orchestrator.blackboard import Blackboard

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run

STATION = "S4:grounding"


async def run(run: Run, services: Services) -> Run:
    """Strip ungrounded evidence ids and drop unsupported proposals."""
    with services.tracer.span("s4_grounding", **span_attrs_for_run(run)):
        run.status = RunStatus.GROUNDING

        blackboard = Blackboard(services.memory)
        proposals = await blackboard.load_proposals(run.id)
        known = {item.id for item in run.evidence}

        survivors: list[Proposal] = []
        for proposal in proposals:
            grounded = [eid for eid in proposal.evidence_ids if eid in known]
            stripped = len(proposal.evidence_ids) - len(grounded)
            proposal.evidence_ids = grounded
            if stripped:
                run.audit.append(
                    _audit(
                        f"stripped {stripped} ungrounded evidence id(s) "
                        f"from proposal {proposal.id}"
                    )
                )
            if not grounded:
                run.audit.append(_audit(f"killed ungrounded proposal {proposal.id}"))
                continue
            survivors.append(proposal)

        await blackboard.save_proposals(run.id, survivors)
        run.proposals = [p.id for p in survivors]
        run.audit.append(
            _audit(f"grounding gate: {len(survivors)} proposal(s) survived of {len(proposals)}")
        )
        return run


def _audit(message: str):
    """Construct an audit record for this station."""
    from dsf.contracts.models import AuditRecord

    return AuditRecord(station=STATION, message=message)


__all__ = ["STATION", "run"]
