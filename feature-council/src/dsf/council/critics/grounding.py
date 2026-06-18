"""Grounding critic — redundant cross-check of the hard grounding gate.

Vetoes any proposal whose ``evidence_ids`` is empty or references an id not
present in ``run.evidence``. Fully-grounded proposals score 1.0, else 0.0.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.models import CriticScore

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run

NAME = "grounding"


async def evaluate(proposal: Proposal, run: Run, services: Services) -> CriticScore:
    """Veto ungrounded proposals; otherwise score 1.0."""
    known = {item.id for item in run.evidence}

    if not proposal.evidence_ids:
        return CriticScore(
            critic=NAME,
            score=0.0,
            veto=True,
            rationale="No evidence_ids — proposal is ungrounded.",
        )

    missing = [eid for eid in proposal.evidence_ids if eid not in known]
    if missing:
        return CriticScore(
            critic=NAME,
            score=0.0,
            veto=True,
            rationale=f"{len(missing)} evidence id(s) not present in run evidence.",
        )

    return CriticScore(
        critic=NAME,
        score=1.0,
        veto=False,
        rationale=f"All {len(proposal.evidence_ids)} evidence id(s) trace to run evidence.",
    )


__all__ = ["NAME", "evaluate"]
