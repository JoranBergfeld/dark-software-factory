"""Synthesizer — cluster run evidence into candidate :class:`Proposal`s.

Robustness contract: this MUST yield >=1 valid proposal for a run that
carries evidence even when the model returns no structured prose. The model is
therefore used *only* for the proposal title; everything else, including the
load-bearing fields ``evidence_ids``, ``product`` and ``kind``, is derived
deterministically by clustering evidence on shared ``product_hints``.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from pydantic import BaseModel

from dsf.contracts.enums import ProposalKind
from dsf.contracts.models import EvidenceItem, Proposal

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Run


class ProposalProse(BaseModel):
    """The model-drafted prose for a proposal (only the title is load-bearing)."""

    title: str


#: Synthesizer prompt tag — handlers may register against this to script a title.
SYNTH_TAG = "[synthesize]"

#: Keywords in claims that bias a cluster toward a FIX (vs FEATURE) proposal.
FIX_KEYWORDS = (
    "error",
    "errors",
    "regression",
    "regressions",
    "exception",
    "crash",
    "crashed",
    "failure",
    "failing",
    "fails",
    "broken",
    "bug",
    "outage",
    "latency",
    "timeout",
    "5xx",
    "500",
    "spike",
    "degraded",
)

#: Bucket key used for evidence that carries no product hint at all.
_NO_PRODUCT = "_unscoped"


def _cluster_by_product(evidence: list[EvidenceItem]) -> dict[str, list[EvidenceItem]]:
    """Group evidence items by their (first) product hint.

    Items with multiple hints land in the bucket of their first hint so each
    item belongs to exactly one cluster. Items with no hints share the
    ``_unscoped`` bucket. Order of first appearance is preserved.
    """
    clusters: dict[str, list[EvidenceItem]] = {}
    for item in evidence:
        key = item.product_hints[0] if item.product_hints else _NO_PRODUCT
        clusters.setdefault(key, []).append(item)
    return clusters


def _looks_like_fix(items: list[EvidenceItem]) -> bool:
    """Heuristic: does this cluster's evidence describe an error/regression?"""
    blob = " ".join(i.claim.lower() for i in items)
    return any(kw in blob for kw in FIX_KEYWORDS)


def _mean_confidence(items: list[EvidenceItem]) -> float:
    """Mean evidence confidence (0.0 for an empty cluster)."""
    if not items:
        return 0.0
    return sum(i.confidence for i in items) / len(items)


async def synthesize(run: Run, services: Services) -> list[Proposal]:
    """Synthesize candidate proposals from a run's evidence.

    Retrieves Decision-Memory lessons per product for context, clusters the
    run's evidence by product hint, and emits one :class:`Proposal` per
    non-empty cluster. ``kind`` is FIX when the cluster's claims look
    error/regression-ish (keyword heuristic) else FEATURE; ``product`` is the
    cluster's hint; ``evidence_ids`` are exactly that cluster's items;
    ``confidence`` is the mean evidence confidence.
    """
    clusters = _cluster_by_product(run.evidence)
    proposals: list[Proposal] = []

    for key, items in clusters.items():
        if not items:
            continue
        product = None if key == _NO_PRODUCT else key

        # Lessons are retrieved for context (per design 5.2); they inform the
        # prose prompt but never the deterministic fields.
        lessons: list[dict] = []
        if product is not None:
            lessons = await services.memory.get_lessons(product)

        kind = ProposalKind.FIX if _looks_like_fix(items) else ProposalKind.FEATURE
        claims = "; ".join(i.claim for i in items)
        lesson_context = " | ".join(str(le.get("text", "")) for le in lessons)

        prompt = (
            f"{SYNTH_TAG} product={product or 'unscoped'} kind={kind.value}\n"
            f"claims: {claims}\n"
            f"lessons: {lesson_context}\n"
            "Draft a concise proposal title, problem statement and proposed change."
        )
        prose = await services.model.complete(
            system="You are the intake synthesizer.",
            prompt=prompt,
            schema=ProposalProse,
        )

        verb = "Fix" if kind is ProposalKind.FIX else "Improve"
        scope = product or "the product"
        fallback_title = f"{verb} {scope}: {items[0].claim}"[:120]
        if isinstance(prose, ProposalProse) and prose.title.strip():
            title = prose.title
        else:
            title = fallback_title
        fallback_problem = (
            f"Evidence ({len(items)} item(s)) indicates: {claims}"
        )
        fallback_change = (
            f"Address the above by {'remediating' if kind is ProposalKind.FIX else 'building'} "
            f"a change scoped to {scope}."
        )

        proposals.append(
            Proposal(
                run_id=run.id,
                kind=kind,
                title=title,
                problem=fallback_problem,
                proposed_change=fallback_change,
                product=product,
                evidence_ids=[i.id for i in items],
                confidence=_mean_confidence(items),
            )
        )

    return proposals


__all__ = ["FIX_KEYWORDS", "SYNTH_TAG", "ProposalProse", "synthesize"]
