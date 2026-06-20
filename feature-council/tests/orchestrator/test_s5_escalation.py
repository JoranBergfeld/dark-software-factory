"""S5 routes ESCALATE outcomes to a human review queue (validation-jury plan)."""

from __future__ import annotations

from dsf.council.jury import JurorDecision
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.stations.s5_council import REVIEW_QUEUE_KIND
from dsf.orchestrator.stations.s5_council import run as s5_run
from dsf_testing import build_test_services, make_evidence, make_proposal, make_run


async def test_escalated_proposal_goes_to_review_queue_not_routed():
    services = build_test_services()
    # Force a 2-1 jury under the default supervised maturity -> ESCALATE.
    services.model.register(
        "[jury:skeptic]", lambda system, prompt: JurorDecision(go=False)
    )

    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")
    bb = Blackboard(services.memory)
    await bb.save_proposals(run.id, [prop])

    result = await s5_run(run, services)

    # Escalated proposals are not routed onward.
    assert result.proposals == []
    # A review-queue record was written for the proposal.
    queued = await services.memory.query_similar(prop.title, REVIEW_QUEUE_KIND, k=5)
    assert any(rec.get("proposal_id") == prop.id for rec in queued)
    # The audit trail records the escalation.
    assert any("escalated" in rec.message.lower() for rec in result.audit)
