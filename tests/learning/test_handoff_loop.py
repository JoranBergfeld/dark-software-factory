"""Closed-loop test: council routing -> handoff label -> squad PR -> lesson.

Asserts the two council-owned halves of the council<->squad knowledge loop
connect: every routed issue carries the handoff label the squad triages on, and
a merged squad PR for that product produces a lesson the next council run can
retrieve. The squad's own ``.squad/`` reflection is external (documented, not
faked here).
"""

from __future__ import annotations

from dsf.container import build_services
from dsf.contracts.enums import ProposalKind, TriggerKind
from dsf.contracts.handoff import HANDOFF_LABEL
from dsf.contracts.models import Proposal, Run
from dsf.learning import handle_pr_event
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.stations import s6_routing

PRODUCT = "microbi"  # present in config/products.json


def _merged_pr_event() -> dict:
    return {
        "action": "closed",
        "pull_request": {
            "id": 501,
            "number": 12,
            "title": "Fix checkout TypeError",
            "state": "closed",
            "merged": True,
            "body": f"Implements council issue.\nproduct: {PRODUCT}\n",
            "labels": [{"name": HANDOFF_LABEL}, {"name": f"product:{PRODUCT}"}],
        },
        "spec_diff": "- crash on null cart\n+ guard null cart",
    }


async def test_council_to_squad_loop_closes():
    services = build_services("local")

    run = Run(trigger=TriggerKind.SIGNAL, scope_product_hints=[PRODUCT])
    proposal = Proposal(
        run_id=run.id,
        kind=ProposalKind.FIX,
        title="Fix checkout TypeError",
        problem="Unhandled TypeError in checkout.",
        proposed_change="Guard the null cart path.",
        product=PRODUCT,
        confidence=0.9,
    )
    await Blackboard(services.memory).save_proposals(run.id, [proposal])

    # Council half: routing stamps the handoff label the squad keys on.
    await s6_routing.run(run, services)
    issues = await Blackboard(services.memory).load_issues(run.id)
    assert issues, "expected a routed issue"
    assert HANDOFF_LABEL in issues[0].labels

    # Squad half: a merged PR for the product feeds the learning loop.
    await handle_pr_event(_merged_pr_event(), services)
    lessons = await services.memory.get_lessons(PRODUCT)
    assert lessons, "merged squad PR should yield a retrievable lesson"
    assert lessons[0]["product"] == PRODUCT
    assert lessons[0]["outcome"] == "approved"
