"""Shared fixtures/helpers for council tests."""

from __future__ import annotations

from dsf.contracts.enums import ProposalKind, SourceKind, TriggerKind
from dsf.contracts.models import EvidenceItem, Proposal, Provenance, Run


def make_evidence(
    claim: str,
    product: str = "alpha",
    *,
    source_agent: str = "sentry-agent",
    source_kind: SourceKind = SourceKind.SENTRY,
    confidence: float = 0.8,
) -> EvidenceItem:
    """Build a well-formed EvidenceItem for tests."""
    return EvidenceItem(
        source_agent=source_agent,
        claim=claim,
        raw_citation=f"sentry://issue/{abs(hash(claim)) % 1000}",
        provenance=Provenance(query_used="q", source_kind=source_kind),
        confidence=confidence,
        product_hints=[product],
    )


def make_run(evidence: list[EvidenceItem]) -> Run:
    """Build a Run carrying the given evidence."""
    return Run(
        trigger=TriggerKind.SIGNAL,
        evidence=evidence,
        scope_product_hints=["alpha"],
    )


def make_proposal(
    run: Run,
    *,
    evidence_ids: list[str] | None = None,
    title: str = "Improve alpha latency",
    problem: str = "alpha p99 latency elevated",
    proposed_change: str = "Add caching to the alpha hot path.",
    product: str | None = "alpha",
    kind: ProposalKind = ProposalKind.FIX,
) -> Proposal:
    """Build a Proposal wired to a run's evidence by default."""
    if evidence_ids is None:
        evidence_ids = [item.id for item in run.evidence]
    return Proposal(
        run_id=run.id,
        kind=kind,
        title=title,
        problem=problem,
        proposed_change=proposed_change,
        product=product,
        evidence_ids=evidence_ids,
        confidence=0.8,
    )
