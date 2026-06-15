"""Feasibility/Risk critic — scores down oversized or risky scope.

Heuristic on ``proposed_change`` length plus risky-scope keywords such as
"rewrite", "migrate everything", "overhaul". No veto.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.models import CriticScore

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run

NAME = "feasibility"

#: Phrases that signal an oversized, high-risk change.
RISKY_KEYWORDS = (
    "rewrite",
    "migrate everything",
    "overhaul",
    "rearchitect",
    "re-architect",
    "from scratch",
    "rip out",
    "big bang",
)

#: ``proposed_change`` length (chars) at which the size penalty maxes out.
_LENGTH_PENALTY_CAP = 600


async def evaluate(proposal: Proposal, run: Run, services: Services) -> CriticScore:
    """Score feasibility, penalizing large/risky scope. No veto."""
    text = proposal.proposed_change
    lowered = text.lower()

    # Size penalty: longer change descriptions imply larger scope.
    length_penalty = min(len(text), _LENGTH_PENALTY_CAP) / _LENGTH_PENALTY_CAP

    risky_hits = [kw for kw in RISKY_KEYWORDS if kw in lowered]
    risk_penalty = min(1.0, 0.4 * len(risky_hits))

    score = max(0.0, 1.0 - 0.5 * length_penalty - risk_penalty)

    return CriticScore(
        critic=NAME,
        score=score,
        veto=False,
        rationale=(
            f"len={len(text)} risky={risky_hits or 'none'} -> feasibility {score:.2f}."
        ),
    )


__all__ = ["NAME", "RISKY_KEYWORDS", "evaluate"]
