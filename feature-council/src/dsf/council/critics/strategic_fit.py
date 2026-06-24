"""Strategic-fit critic — deterministic neutral baseline for the lens.

``strategic_fit`` is a *lens*: during deliberation it states a position through
the model against the product charter (see :mod:`dsf.council.deliberation`), and
this deterministic ``evaluate`` is only the fallback used when the model returns
no structured position. Charter alignment is a matter of judgment, not a keyword
count, so the deterministic baseline is the neutral 0.6. No veto.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.models import CriticScore

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run

NAME = "strategic_fit"

#: Neutral score: the model-driven lens does charter alignment; this is its floor.
DEFAULT_SCORE = 0.6


async def evaluate(proposal: Proposal, run: Run, services: Services) -> CriticScore:
    """Neutral strategic-fit baseline (alignment is judged by the lens). No veto."""
    return CriticScore(
        critic=NAME,
        score=DEFAULT_SCORE,
        veto=False,
        rationale=(
            f"Neutral strategic-fit baseline {DEFAULT_SCORE:.2f} "
            "(charter alignment is judged by the deliberation lens)."
        ),
    )


__all__ = ["NAME", "DEFAULT_SCORE", "evaluate"]
