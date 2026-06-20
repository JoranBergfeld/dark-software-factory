"""Shared, dependency-light test doubles and builders for the DSF suite.

This lives at the workspace root (outside any member's ``src/``) and is put on
``sys.path`` via the pytest ``pythonpath`` setting, so every member's tests can
``from dsf_testing import ...`` regardless of where they sit. It depends only on
``dsf-core`` contracts and ports -- never on an application member -- so it does
not couple members to one another.
"""

from __future__ import annotations

from dsf.contracts.enums import ProposalKind, SourceKind, TriggerKind
from dsf.contracts.models import EvidenceItem, Proposal, Provenance, Run
from dsf_testing.config import InMemoryConfigStore
from dsf_testing.github import RecordingGitHubClient
from dsf_testing.memory import InMemoryMemoryStore
from dsf_testing.model import ECHO_PREFIX, DeterministicModelClient
from dsf_testing.services import build_test_services
from dsf_testing.tracing import NoOpTracer


class RecordingSourceBackend:
    """Source backend double returning a fixed list of evidence.

    Satisfies the :class:`~dsf.ports.SourceBackend` protocol without any I/O and
    records each ``gather`` call. Shared by the ``a2a`` (core) and ``agents``
    (feature-council) suites.
    """

    def __init__(self, evidence: list[EvidenceItem] | None = None) -> None:
        self._evidence: list[EvidenceItem] = list(evidence or [])
        self.calls: list[dict] = []

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Record the call and return a copy of the provided evidence."""
        self.calls.append(dict(run_scope))
        return list(self._evidence)


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


__all__ = [
    "ECHO_PREFIX",
    "DeterministicModelClient",
    "InMemoryConfigStore",
    "InMemoryMemoryStore",
    "NoOpTracer",
    "RecordingGitHubClient",
    "RecordingSourceBackend",
    "build_test_services",
    "make_evidence",
    "make_proposal",
    "make_run",
]
