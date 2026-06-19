"""Tests for the shared contract models (plan Task 0.2)."""

from __future__ import annotations

import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from dsf.contracts.enums import (
    ProposalKind,
    RunStatus,
    SourceKind,
    TriggerKind,
    Verdict,
)
from dsf.contracts.export_schema import TOP_LEVEL_MODELS, export_schemas
from dsf.contracts.models import (
    CouncilVerdict,
    CriticScore,
    EvidenceItem,
    JurorVote,
    JuryResult,
    Proposal,
    Provenance,
    Run,
)


def _evidence(**kwargs) -> EvidenceItem:
    base = dict(
        source_agent="sentry",
        claim="Error rate spiked 5x",
        raw_citation="sentry://issue/123",
        provenance=Provenance(query_used="errors", source_kind=SourceKind.SENTRY),
        confidence=0.8,
    )
    base.update(kwargs)
    return EvidenceItem(**base)


def test_evidence_round_trip_and_defaults():
    item = _evidence()
    assert item.id
    assert item.created_at is not None
    dumped = item.model_dump()
    restored = EvidenceItem.model_validate(dumped)
    assert restored.raw_citation == "sentry://issue/123"
    assert restored.source_agent == "sentry"


@pytest.mark.parametrize("bad", ["", "   ", "\t\n"])
def test_evidence_rejects_blank_raw_citation(bad):
    with pytest.raises(ValidationError):
        _evidence(raw_citation=bad)


def test_run_defaults():
    run = Run(trigger=TriggerKind.SIGNAL)
    assert run.status is RunStatus.OPEN
    assert run.evidence == []
    assert run.proposals == []
    assert run.dry_run is False
    assert run.audit == []
    assert run.id


def test_council_verdict_kill_on_veto():
    scores = [
        CriticScore(critic="value", score=0.95, veto=False),
        CriticScore(critic="security", score=0.9, veto=True, rationale="bad content"),
    ]
    verdict = CouncilVerdict.from_scores("prop-1", scores, threshold=0.6)
    assert verdict.verdict is Verdict.KILL
    assert "security" in verdict.rationale


def test_council_verdict_accept_above_threshold():
    scores = [
        CriticScore(critic="value", score=0.8),
        CriticScore(critic="cost", score=0.7),
    ]
    verdict = CouncilVerdict.from_scores("prop-2", scores, threshold=0.6)
    assert verdict.verdict is Verdict.ACCEPT
    assert verdict.weighted_score == pytest.approx(0.75)


def test_council_verdict_kill_below_threshold():
    scores = [CriticScore(critic="value", score=0.3)]
    verdict = CouncilVerdict.from_scores("prop-3", scores, threshold=0.6)
    assert verdict.verdict is Verdict.KILL


def test_council_verdict_weights_applied():
    scores = [
        CriticScore(critic="value", score=1.0),
        CriticScore(critic="cost", score=0.0),
    ]
    # Heavily weight value so the mean clears threshold.
    verdict = CouncilVerdict.from_scores(
        "prop-4", scores, threshold=0.6, weights={"value": 3.0, "cost": 1.0}
    )
    assert verdict.weighted_score == pytest.approx(0.75)
    assert verdict.verdict is Verdict.ACCEPT


def test_proposal_fields():
    prop = Proposal(
        run_id="run-1",
        kind=ProposalKind.FEATURE,
        title="Add retry",
        problem="No retry",
        proposed_change="Add retry logic",
        evidence_ids=["e1"],
    )
    assert prop.product is None
    assert prop.kind is ProposalKind.FEATURE


def test_verdict_has_escalate_outcome():
    assert Verdict.ESCALATE.value == "ESCALATE"
    assert Verdict.ESCALATE not in (Verdict.ACCEPT, Verdict.KILL)


def test_jury_result_reports_fraction_consensus_majority():
    jr = JuryResult(
        votes=[
            JurorVote(juror="a", go=True),
            JurorVote(juror="b", go=True),
            JurorVote(juror="c", go=False),
        ]
    )
    assert abs(jr.go_fraction - 2 / 3) < 1e-9
    assert jr.majority_go is True
    assert abs(jr.consensus - 2 / 3) < 1e-9


def test_jury_result_empty_has_no_consensus():
    jr = JuryResult()
    assert jr.go_fraction == 0.0
    assert jr.consensus == 0.0
    assert jr.majority_go is False


def test_export_schemas(tmp_path: Path):
    written = export_schemas(out_dir=tmp_path)
    assert len(written) == len(TOP_LEVEL_MODELS)
    for path in written:
        assert path.exists()
        data = json.loads(path.read_text(encoding="utf-8"))
        assert "properties" in data or "$defs" in data
