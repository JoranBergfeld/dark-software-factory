"""Value/Impact critic — scores proposals by evidentiary weight.

Score scales with the number of supporting evidence items and the presence
of HIGH/CRITICAL severity signals in their claims/hints. No veto.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.models import CriticScore

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import EvidenceItem, Proposal, Run

NAME = "value"

#: Severity terms that bump perceived value when present in evidence.
HIGH_SEVERITY_TERMS = ("high", "critical", "sev1", "sev0", "p0", "p1", "urgent")

#: Evidence count at which the count-driven component saturates.
_COUNT_SATURATION = 4


def _supporting(proposal: Proposal, run: Run) -> list[EvidenceItem]:
    """Run evidence items referenced by the proposal."""
    ids = set(proposal.evidence_ids)
    return [item for item in run.evidence if item.id in ids]


def _has_high_severity(items: list[EvidenceItem]) -> bool:
    """Whether any supporting item carries a high/critical severity signal."""
    for item in items:
        blob = (item.claim + " " + " ".join(item.product_hints)).lower()
        if any(term in blob for term in HIGH_SEVERITY_TERMS):
            return True
    return False


async def evaluate(proposal: Proposal, run: Run, services: Services) -> CriticScore:
    """Score impact from supporting evidence count + severity. No veto."""
    items = _supporting(proposal, run)
    count = len(items)

    # Count component saturates so a handful of items already reads as valuable.
    count_score = min(count, _COUNT_SATURATION) / _COUNT_SATURATION

    severity_bonus = 0.3 if _has_high_severity(items) else 0.0
    score = min(1.0, 0.6 * count_score + severity_bonus + (0.1 if count else 0.0))

    return CriticScore(
        critic=NAME,
        score=score,
        veto=False,
        rationale=(
            f"{count} supporting item(s); "
            f"high-severity={'yes' if severity_bonus else 'no'} -> value {score:.2f}."
        ),
    )


__all__ = ["NAME", "HIGH_SEVERITY_TERMS", "evaluate"]
