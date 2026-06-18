"""Security/Compliance critic — screens proposal content before filing.

Vetoes when the proposal text contains any flagged term from a deterministic
list (e.g. "store plaintext password", "disable auth", "ship secret").
Otherwise scores 1.0.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.models import CriticScore

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run

NAME = "security"

#: Deterministic deny-list of clearly unsafe proposal content.
FLAGGED_TERMS = (
    "store plaintext password",
    "plaintext password",
    "disable auth",
    "disable authentication",
    "ship secret",
    "hardcode secret",
    "hardcoded secret",
    "disable encryption",
    "skip validation",
    "bypass auth",
)


async def evaluate(proposal: Proposal, run: Run, services: Services) -> CriticScore:
    """Veto on flagged unsafe content; otherwise score 1.0."""
    blob = f"{proposal.title}\n{proposal.problem}\n{proposal.proposed_change}".lower()
    hits = [term for term in FLAGGED_TERMS if term in blob]

    if hits:
        return CriticScore(
            critic=NAME,
            score=0.0,
            veto=True,
            rationale=f"Flagged unsafe content: {hits}.",
        )

    return CriticScore(
        critic=NAME,
        score=1.0,
        veto=False,
        rationale="No flagged security/compliance terms found.",
    )


__all__ = ["NAME", "FLAGGED_TERMS", "evaluate"]
