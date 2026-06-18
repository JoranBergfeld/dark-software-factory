"""S5 — Critic Council (agentic workcell).

Run the council :func:`decide` over each surviving proposal. Keep only ACCEPT
verdicts; killed proposals are dropped, audited, and written to the kill log in
memory (the audit trail of a dark system). Surviving proposals + their verdicts
are persisted to the blackboard.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.enums import RunStatus, Verdict
from dsf.council.decision import decide
from dsf.observability.tracing import span_attrs_for_run
from dsf.orchestrator.blackboard import Blackboard

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import CouncilVerdict, Proposal, Run

STATION = "S5:council"

#: Memory record-kind for the accept/kill decision log.
KILL_LOG_KIND = "kill_log"


async def run(run: Run, services: Services) -> Run:
    """Decide each proposal; keep ACCEPTs, log KILLs."""
    with services.tracer.span("s5_council", **span_attrs_for_run(run)):
        run.status = RunStatus.COUNCIL

        blackboard = Blackboard(services.memory)
        proposals = await blackboard.load_proposals(run.id)

        accepted: list[Proposal] = []
        verdicts: list[CouncilVerdict] = []
        for proposal in proposals:
            verdict = await decide(proposal, run, services)
            verdicts.append(verdict)
            if verdict.verdict == Verdict.ACCEPT:
                accepted.append(proposal)
                # #3: index this proposal so future runs can detect duplicates
                await services.memory.put_record({
                    "kind": "proposal",
                    "text": f"{proposal.title} {proposal.problem}",
                    "proposal_id": proposal.id,
                    "run_id": run.id,
                })
                # #4: persist per-critic scores for later calibration join
                await services.memory.put_working(
                    f"critic_scores:{proposal.id}",
                    {s.critic: s.score for s in verdict.scores},
                )
            else:
                run.audit.append(_audit(f"council killed {proposal.id}: {verdict.rationale}"))
                await services.memory.put_record(
                    {
                        "kind": KILL_LOG_KIND,
                        "run_id": run.id,
                        "proposal_id": proposal.id,
                        "verdict": verdict.verdict.value,
                        "weighted_score": verdict.weighted_score,
                        "threshold": verdict.threshold,
                        "text": f"{proposal.title} :: {verdict.rationale}",
                    }
                )

        await blackboard.save_proposals(run.id, accepted)
        await blackboard.save_verdicts(run.id, verdicts)
        run.proposals = [p.id for p in accepted]
        run.audit.append(
            _audit(f"council: {len(accepted)} accepted, {len(proposals) - len(accepted)} killed")
        )
        return run


def _audit(message: str):
    """Construct an audit record for this station."""
    from dsf.contracts.models import AuditRecord

    return AuditRecord(station=STATION, message=message)


__all__ = ["STATION", "KILL_LOG_KIND", "run"]
