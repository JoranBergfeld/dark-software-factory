"""Strategic-fit critic — scores alignment with product roadmap/lessons.

Reads Decision-Memory lessons (FoundryIQ roadmap/company-knowledge hints) for
the proposal's product. Supportive lessons raise the score above the neutral
default of 0.6; no veto.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.models import CriticScore

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run

NAME = "strategic_fit"

#: Neutral score when there is nothing for/against strategic fit.
DEFAULT_SCORE = 0.6

#: Lesson sentiment terms that count as supportive of a proposal.
SUPPORTIVE_TERMS = ("roadmap", "priority", "strategic", "invest", "aligned", "supported")


async def evaluate(proposal: Proposal, run: Run, services: Services) -> CriticScore:
    """Score strategic fit from supportive product lessons. No veto."""
    if proposal.product is None:
        return CriticScore(
            critic=NAME,
            score=DEFAULT_SCORE,
            veto=False,
            rationale="No product scope; neutral strategic fit.",
        )

    lessons = await services.memory.get_lessons(proposal.product)
    supportive = 0
    for le in lessons:
        blob = str(le.get("text", "")).lower()
        if any(term in blob for term in SUPPORTIVE_TERMS):
            supportive += 1

    score = min(1.0, DEFAULT_SCORE + 0.2 * supportive)

    return CriticScore(
        critic=NAME,
        score=score,
        veto=False,
        rationale=(
            f"{len(lessons)} lesson(s), {supportive} supportive -> strategic fit {score:.2f}."
        ),
    )


__all__ = ["NAME", "DEFAULT_SCORE", "SUPPORTIVE_TERMS", "evaluate"]
