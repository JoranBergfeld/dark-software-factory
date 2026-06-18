"""Duplication/Prior-art critic — vetoes proposals already seen.

Uses :func:`dsf.memory.dedup.is_duplicate` against the memory store's
``proposal`` records. A near-duplicate is vetoed; otherwise scores 1.0.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.models import CriticScore
from dsf.memory.dedup import is_duplicate

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run

NAME = "duplication"

#: Memory record kind queried for prior proposals.
RECORD_KIND = "proposal"


async def evaluate(proposal: Proposal, run: Run, services: Services) -> CriticScore:
    """Veto if a near-duplicate proposal already exists in memory."""
    text = f"{proposal.title} {proposal.problem}"
    duplicate = await is_duplicate(text, services.memory, kind=RECORD_KIND)

    if duplicate:
        return CriticScore(
            critic=NAME,
            score=0.0,
            veto=True,
            rationale="Near-duplicate of an existing proposal in memory.",
        )

    return CriticScore(
        critic=NAME,
        score=1.0,
        veto=False,
        rationale="No matching prior proposal found.",
    )


__all__ = ["NAME", "RECORD_KIND", "evaluate"]
