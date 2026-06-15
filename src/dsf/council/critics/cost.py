"""Cost-to-build critic — scores inverse to an effort heuristic.

Longer / more-complex ``proposed_change`` text implies more effort and so a
lower score. No veto.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.models import CriticScore

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run

NAME = "cost"

#: Effort-bearing terms that each add to the estimated build cost.
EFFORT_TERMS = (
    "migrate",
    "refactor",
    "infrastructure",
    "schema",
    "integration",
    "multiple",
    "rewrite",
    "overhaul",
    "redesign",
)

#: Word count at which the length component of effort saturates.
_WORD_SATURATION = 80


async def evaluate(proposal: Proposal, run: Run, services: Services) -> CriticScore:
    """Score inversely to estimated effort. No veto."""
    text = proposal.proposed_change
    words = len(text.split())
    lowered = text.lower()

    length_effort = min(words, _WORD_SATURATION) / _WORD_SATURATION
    term_hits = sum(1 for term in EFFORT_TERMS if term in lowered)
    term_effort = min(1.0, 0.2 * term_hits)

    effort = min(1.0, 0.6 * length_effort + term_effort)
    score = max(0.0, 1.0 - effort)

    return CriticScore(
        critic=NAME,
        score=score,
        veto=False,
        rationale=(
            f"words={words} effort_terms={term_hits} effort={effort:.2f} -> cost score {score:.2f}."
        ),
    )


__all__ = ["NAME", "EFFORT_TERMS", "evaluate"]
