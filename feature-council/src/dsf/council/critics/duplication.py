"""Duplication/Prior-art critic — vetoes proposals already filed.

Uses :func:`dsf.memory.dedup.is_duplicate` against the filed-issue corpus
(:data:`dsf.memory.dedup.FILED_ISSUE_KIND`, the same records S7 writes), keyed
on title + problem. A near-duplicate is vetoed; otherwise scores 1.0.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.models import CriticScore
from dsf.memory.dedup import FILED_ISSUE_KIND, dedup_key, is_duplicate

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run

NAME = "duplication"

#: Memory record kind queried for prior filed issues (shared with S7).
RECORD_KIND = FILED_ISSUE_KIND


async def evaluate(proposal: Proposal, run: Run, services: Services) -> CriticScore:
    """Veto if a near-duplicate of an already-filed issue exists in memory."""
    text = dedup_key(proposal.title, proposal.problem)
    duplicate = await is_duplicate(text, services.memory, kind=RECORD_KIND)

    if duplicate:
        return CriticScore(
            critic=NAME,
            score=0.0,
            veto=True,
            rationale="Near-duplicate of an already-filed issue in memory.",
        )

    return CriticScore(
        critic=NAME,
        score=1.0,
        veto=False,
        rationale="No matching prior filed issue found.",
    )


__all__ = ["NAME", "RECORD_KIND", "evaluate"]
