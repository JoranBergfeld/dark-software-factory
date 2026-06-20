"""Validation jury tests (validation-jury plan)."""

from __future__ import annotations

from dsf.contracts.enums import Verdict
from dsf.contracts.models import CouncilVerdict, CriticScore
from dsf.council.jury import convene_jury
from dsf_testing import build_test_services, make_evidence, make_proposal, make_run


def _accept_recommendation(proposal_id: str) -> CouncilVerdict:
    return CouncilVerdict.from_scores(
        proposal_id, [CriticScore(critic="value", score=1.0)], threshold=0.6
    )


async def test_jury_offline_echoes_accept_recommendation():
    services = build_test_services()
    run = make_run([make_evidence("x")])
    prop = make_proposal(run)
    rec = _accept_recommendation(prop.id)
    assert rec.verdict == Verdict.ACCEPT

    jury = await convene_jury(rec, prop, run, services)

    assert len(jury.votes) == 3
    assert all(v.go for v in jury.votes)
    assert jury.consensus == 1.0


async def test_jury_offline_echoes_kill_recommendation():
    services = build_test_services()
    run = make_run([make_evidence("x")])
    prop = make_proposal(run)
    rec = CouncilVerdict.from_scores(prop.id, [], threshold=0.6)  # no scores -> KILL
    assert rec.verdict == Verdict.KILL

    jury = await convene_jury(rec, prop, run, services)

    assert all(not v.go for v in jury.votes)
    assert jury.consensus == 1.0
    assert jury.majority_go is False


async def test_jury_splits_when_one_model_dissents():
    services = build_test_services()
    services.model.register("[jury:skeptic]", lambda system, prompt: "NO-GO: evidence too thin")
    run = make_run([make_evidence("x")])
    prop = make_proposal(run)
    rec = _accept_recommendation(prop.id)

    jury = await convene_jury(rec, prop, run, services)

    assert sum(1 for v in jury.votes if v.go) == 2
    assert jury.majority_go is True
    assert abs(jury.go_fraction - 2 / 3) < 1e-9
    skeptic = next(v for v in jury.votes if v.juror == "skeptic")
    assert skeptic.go is False
    assert "thin" in skeptic.rationale.lower()
