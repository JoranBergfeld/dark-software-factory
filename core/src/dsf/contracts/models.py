"""Pydantic v2 models — the shared blackboard contracts.

Run -> EvidenceItem[] -> Proposal -> CouncilVerdict -> RoutedIssue.

All top-level models carry a deterministic ``id`` (uuid4 hex) and a
``created_at`` timestamp by default. ``EvidenceItem`` enforces a non-empty
``raw_citation`` because that is what the grounding gate verifies.
"""

from __future__ import annotations

import uuid
from datetime import UTC, datetime

from pydantic import BaseModel, Field, field_validator

from dsf.contracts.enums import (
    ProposalKind,
    RunStatus,
    SourceKind,
    TriggerKind,
    Verdict,
)


def _new_id() -> str:
    """Generate a fresh uuid4 hex id."""
    return uuid.uuid4().hex


def _now() -> datetime:
    """Current UTC timestamp."""
    return datetime.now(UTC)


class Provenance(BaseModel):
    """Where/when/how an evidence claim was obtained."""

    timestamp: datetime = Field(default_factory=_now)
    query_used: str
    source_kind: SourceKind


class EvidenceItem(BaseModel):
    """A single grounded claim emitted by a source agent."""

    id: str = Field(default_factory=_new_id)
    created_at: datetime = Field(default_factory=_now)
    source_agent: str
    claim: str
    raw_citation: str
    provenance: Provenance
    confidence: float = 0.0
    product_hints: list[str] = Field(default_factory=list)

    @field_validator("raw_citation")
    @classmethod
    def _raw_citation_not_blank(cls, value: str) -> str:
        """Reject empty/blank raw citations — grounding depends on them."""
        if value is None or not value.strip():
            raise ValueError("raw_citation must be a non-empty, non-blank string")
        return value


class AuditRecord(BaseModel):
    """One audit-trail line written by a station."""

    id: str = Field(default_factory=_new_id)
    created_at: datetime = Field(default_factory=_now)
    station: str
    message: str


class Run(BaseModel):
    """A conveyor run — the central blackboard object."""

    id: str = Field(default_factory=_new_id)
    created_at: datetime = Field(default_factory=_now)
    trigger: TriggerKind
    status: RunStatus = RunStatus.OPEN
    scope_product_hints: list[str] = Field(default_factory=list)
    source_kinds: list[SourceKind] = Field(default_factory=list)
    signal_payload: dict = Field(default_factory=dict)
    evidence: list[EvidenceItem] = Field(default_factory=list)
    proposals: list[str] = Field(default_factory=list)
    dry_run: bool = False
    audit: list[AuditRecord] = Field(default_factory=list)


class Proposal(BaseModel):
    """A candidate proposal synthesized from clustered evidence."""

    id: str = Field(default_factory=_new_id)
    created_at: datetime = Field(default_factory=_now)
    run_id: str
    kind: ProposalKind
    title: str
    problem: str
    proposed_change: str
    product: str | None = None
    evidence_ids: list[str] = Field(default_factory=list)
    confidence: float = 0.0


class CriticScore(BaseModel):
    """One critic's score on a proposal."""

    critic: str
    score: float
    veto: bool = False
    rationale: str = ""


class JurorVote(BaseModel):
    """One juror's go / no-go vote validating a council recommendation."""

    juror: str
    go: bool
    rationale: str = ""


class JuryResult(BaseModel):
    """The validation jury's panel of votes over a recommendation."""

    votes: list[JurorVote] = Field(default_factory=list)

    @property
    def go_fraction(self) -> float:
        """Fraction of jurors voting to proceed (0.0 when no votes)."""
        if not self.votes:
            return 0.0
        return sum(1 for v in self.votes if v.go) / len(self.votes)

    @property
    def majority_go(self) -> bool:
        """Whether a strict majority voted to proceed."""
        return self.go_fraction > 0.5

    @property
    def consensus(self) -> float:
        """Agreement strength of the majority side (1.0 = unanimous)."""
        if not self.votes:
            return 0.0
        go = self.go_fraction
        return max(go, 1.0 - go)


class CouncilVerdict(BaseModel):
    """The council's aggregated decision on a proposal."""

    id: str = Field(default_factory=_new_id)
    created_at: datetime = Field(default_factory=_now)
    proposal_id: str
    verdict: Verdict
    weighted_score: float
    threshold: float
    scores: list[CriticScore] = Field(default_factory=list)
    jury: JuryResult | None = None
    rationale: str = ""

    @classmethod
    def from_scores(
        cls,
        proposal_id: str,
        scores: list[CriticScore],
        threshold: float,
        weights: dict[str, float] | None = None,
    ) -> CouncilVerdict:
        """Build a verdict from critic scores.

        Decision rule: any hard veto kills; otherwise the weighted mean score
        must clear ``threshold`` to ACCEPT.
        """
        weights = weights or {}
        vetoes = [s for s in scores if s.veto]

        total_weight = 0.0
        weighted_sum = 0.0
        for s in scores:
            w = weights.get(s.critic, 1.0)
            total_weight += w
            weighted_sum += w * s.score
        weighted_score = weighted_sum / total_weight if total_weight else 0.0

        if vetoes:
            verdict = Verdict.KILL
            names = ", ".join(s.critic for s in vetoes)
            rationale = f"KILL: hard veto from {names}."
        elif weighted_score >= threshold:
            verdict = Verdict.ACCEPT
            rationale = (
                f"ACCEPT: weighted score {weighted_score:.3f} >= threshold {threshold:.3f}."
            )
        else:
            verdict = Verdict.KILL
            rationale = (
                f"KILL: weighted score {weighted_score:.3f} < threshold {threshold:.3f}."
            )

        return cls(
            proposal_id=proposal_id,
            verdict=verdict,
            weighted_score=weighted_score,
            threshold=threshold,
            scores=scores,
            rationale=rationale,
        )


class RoutedIssue(BaseModel):
    """A proposal routed to a product/repo and shaped into an issue."""

    id: str = Field(default_factory=_new_id)
    created_at: datetime = Field(default_factory=_now)
    proposal_id: str
    product: str
    repo: str
    title: str
    body: str
    labels: list[str] = Field(default_factory=list)
    filed_url: str | None = None
